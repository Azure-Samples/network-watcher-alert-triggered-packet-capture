using System.IO;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.WebJobs.Host;
using Newtonsoft.Json;
using System.Threading.Tasks;
using Microsoft.Azure.Management.Compute.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Authentication;
using Microsoft.Azure.Management.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Azure.Management.Network.Fluent;
using System.Collections.Generic;
using System.Linq;
using System;

namespace AlertPacketCapture
{
    public static class AlertPacketCapture
    {
        [FunctionName("AlertPacketCapture")]
        public static async Task Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = null)]HttpRequest req, TraceWriter log)
        {
            log.Info("C# HTTP trigger function processed a request.");

            //Parse alert request
            string requestBody = new StreamReader(req.Body).ReadToEnd();
            Webhook data = new Webhook();
            data = JsonConvert.DeserializeObject<Webhook>(requestBody);
            Context alertResource = data.RequestBody.context;

            IConfigurationRoot config = new ConfigurationBuilder()
                .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();

            string tenantId = config.GetConnectionString("TenantId");
            string clientId = config.GetConnectionString("clientId");
            string clientKey = config.GetConnectionString("ClientKey");
            if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientKey))
            {
                log.Error("Serivice credentials are null. Check connection string settings");
                return;
            }

            AzureCredentials credentials = SdkContext.AzureCredentialsFactory.FromServicePrincipal(clientId, clientKey, tenantId, AzureEnvironment.AzureGlobalCloud);
            IAzure azure = Azure.Configure().Authenticate(credentials).WithSubscription(alertResource.subscriptionId);
            if (azure == null)
            {
                log.Error("Error: Issues logging into Azure subscription: " + alertResource.subscriptionId + ". Exiting.");
                return;
            }

            IVirtualMachine VM = await azure.VirtualMachines.GetByIdAsync(alertResource.resourceId);
            if (VM == null)
            {
                log.Error("Error: VM: " + alertResource.resourceId + "was not found. Exiting.");
                return;
            }

            INetworkWatcher networkWatcher = await EnsureNetworkWatcherExists(azure, VM.Region, log);

            InstallNetworkWatcherExtension(VM, log);

            string storageAccountId = Environment.GetEnvironmentVariable("PacketCaptureStorageAccount");
            var storageAccount = await azure.StorageAccounts.GetByIdAsync(storageAccountId);
            if (storageAccount == null)
            {
                log.Error("Storage Account: " + storageAccountId + " not found. Exiting.");
                return;
            }

            string packetCaptureName = VM.Name.Substring(0, System.Math.Min(63, VM.Name.Length)) + System.DateTime.Now.ToString("s").Replace(":", "");

            IPacketCaptures packetCapturesObj = networkWatcher.PacketCaptures;
            var packetCaptures = packetCapturesObj.List().ToList();
            if (packetCaptures.Count >= 10)
            {
                log.Info("More than 10 Captures, finding oldest.");
                var packetCaptureTasks = new List<Task<IPacketCaptureStatus>>();
                foreach (IPacketCapture pcap in packetCaptures)
                    packetCaptureTasks.Add(pcap.GetStatusAsync());

                var packetCaptureStatuses = new List<Tuple<IPacketCapture, IPacketCaptureStatus>>();
                for (int i = 0; i < packetCaptureTasks.Count; ++i)
                    packetCaptureStatuses.Add(new Tuple<IPacketCapture, IPacketCaptureStatus>(packetCaptures[i], await packetCaptureTasks[i]));

                packetCaptureStatuses.Sort((Tuple<IPacketCapture, IPacketCaptureStatus> first, Tuple<IPacketCapture, IPacketCaptureStatus> second) =>
                {
                    return first.Item2.CaptureStartTime.CompareTo(second.Item2.CaptureStartTime);
                });
                log.Info("Removing: " + packetCaptureStatuses.First().Item1.Name);
                await networkWatcher.PacketCaptures.DeleteByNameAsync(packetCaptureStatuses.First().Item1.Name);
            }

            log.Info("Creating Packet Capture");
            await networkWatcher.PacketCaptures
                .Define(packetCaptureName)
                .WithTarget(VM.Id)
                .WithStorageAccountId(storageAccount.Id)
                .WithTimeLimitInSeconds(15)
                .CreateAsync();
            log.Info("Packet Capture created successfully");
        }

        private static async Task<INetworkWatcher> EnsureNetworkWatcherExists(IAzure azure, Region region, TraceWriter log = null)
        {
            // Retrieve appropriate Network Watcher, or create one
            INetworkWatcher networkWatcher = azure.NetworkWatchers.List().First(x => x.Region == region);
            if (networkWatcher == null)
            {
                try
                {
                    // Create Resource Group for Network Watcher if Network Watcher does not exist
                    IResourceGroup networkWatcherRG = azure.ResourceGroups.GetByName("NetworkWatcherRG");
                    if (networkWatcherRG == null)
                    {
                        // The RG is conventionally created in USWestCentral even though the Network Watcher region may be different
                        networkWatcherRG = await azure.ResourceGroups
                            .Define("NetworkWatcherRG")
                            .WithRegion(Region.USWestCentral)
                            .CreateAsync();
                    }

                    string networkWatcherName = "NetworkWatcher_" + region.Name.ToString().ToLower();
                    networkWatcher = await azure.NetworkWatchers.Define(networkWatcherName).WithRegion(region).WithExistingResourceGroup(networkWatcherRG).CreateAsync();
                }
                catch (Exception ex)
                {
                    log?.Error($"Unable to create ResourceGroup or Network Watcher: {ex}. Exiting.");
                    throw;
                }
            }

            return networkWatcher;
        }

        private static void InstallNetworkWatcherExtension(IVirtualMachine vm, TraceWriter log = null)
        {
            IVirtualMachineExtension extension = vm.ListExtensions().First(x => x.Value.PublisherName == "Microsoft.Azure.NetworkWatcher").Value;
            if (extension == null)
            {
                vm.Update()
                    .DefineNewExtension("packetcapture")
                    .WithPublisher("Microsoft.Azure.NetworkWatcher")
                    .WithType("NetworkWatcherAgentWindows") // TODO: determine OS family, can be NetworkWatcherAgentLinux
                    .WithVersion("1.4")
                    .Attach();

                log?.Info("Installed Extension on " + vm.Name);
            }
        }
    }
}
