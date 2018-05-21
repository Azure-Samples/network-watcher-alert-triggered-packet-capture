using System.IO;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.WebJobs.Host;
using Newtonsoft.Json.Linq;
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
            try
            {
                log.Info("C# HTTP trigger function processed a request.");

                //Parse alert request
                log.Verbose($"Parsing Alert Request with content of length: {req.ContentLength}");
                string requestBody = new StreamReader(req.Body).ReadToEnd();

                dynamic contextData = JObject.Parse(requestBody).SelectToken("$..context");

                string curSubscriptionId = contextData?.subscriptionId;
                string curResourceGroupName = contextData?.resourceGroupName;
                string curResourceRegion = contextData?.resourceRegion;
                string curResourceName = contextData?.resourceName;
                string curResourceId = contextData?.resourceId;
                string displayContext = $"SubscriptionID: { curSubscriptionId}\nResourceGroupName: { curResourceGroupName} \nResourceRegion: { curResourceRegion} \nResourceName: { curResourceName} \nResourceId: { curResourceId}";
                if (
                    (curSubscriptionId == null) ||
                    (curResourceGroupName == null) ||
                    (curResourceRegion == null) ||
                    (curResourceName == null) ||
                    (curResourceId == null))
                {
                    log.Error($"Insufficient context sent by webhook: \n{displayContext}");
                    throw new Exception("Insufficient context sent by webhook");

                }
                log.Verbose($"Context from webhook parsed: \n{displayContext}");


                IConfigurationRoot config = new ConfigurationBuilder()
                    .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
                    .AddEnvironmentVariables()
                    .Build();
                log.Verbose($"Getting Configuration");
                string tenantId = config.GetConnectionString("TenantId");
                string clientId = config.GetConnectionString("clientId");
                string clientKey = config.GetConnectionString("ClientKey");
                if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientKey))
                {
                    log.Error("Service credentials are null. Check connection string settings");
                    throw new Exception("Service credentials are null");
                }

                log.Verbose($"Getting Credentials");
                AzureCredentials credentials = SdkContext.AzureCredentialsFactory.FromServicePrincipal(clientId, clientKey, tenantId, AzureEnvironment.AzureGlobalCloud);
                IAzure azure = Azure.Configure().Authenticate(credentials).WithSubscription(curSubscriptionId);
                if (azure == null)
                {
                    log.Error("Error: Issues logging into Azure subscription: " + curSubscriptionId + ". Exiting.");
                    throw new Exception("Unable to log into Azure");
                }
                log.Verbose("Azure Credentials successfully created");


                log.Verbose("Obtaining VM");
                IVirtualMachine VM = await azure.VirtualMachines.GetByIdAsync(curResourceId);
                if (VM == null)
                {
                    log.Error($"Error: VM: {curResourceId} was not found. Exiting.");
                    throw new Exception("VM not found");
                }
                log.Verbose($"VM found: {curResourceName}; {curResourceId}");

                log.Verbose($"Checking for Network Watcher in region: {VM.Region}");
                INetworkWatcher networkWatcher = await EnsureNetworkWatcherExists(azure, VM.Region, log);

                log.Verbose($"Checking for Network Watcher Extension on VM: {VM.Name}");
                InstallNetworkWatcherExtension(VM, log);

                log.Verbose("Looking for Storage Account");
                string storageAccountId = Environment.GetEnvironmentVariable("PacketCaptureStorageAccount");
                var storageAccount = await azure.StorageAccounts.GetByIdAsync(storageAccountId);
                if (storageAccount == null)
                {
                    log.Error("Storage Account: " + storageAccountId + " not found. Exiting.");
                    throw new Exception("Storage Account not found");
                }
                log.Verbose("Storage Account found");

                string packetCaptureName = VM.Name.Substring(0, System.Math.Min(50, VM.Name.Length)) + System.DateTime.Now.ToString("yyyyMMddhhmmss").Replace(":", "");
                log.Verbose($"Packet Capture Name: {packetCaptureName}");

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
            catch (Exception err)
            {
                string unhandledErrString = $"Function Ending: error in AlertPacketCapture: {err.ToString()}";
                log.Error(unhandledErrString);
                throw new Exception(unhandledErrString);
            }
        }

        private static async Task<INetworkWatcher> EnsureNetworkWatcherExists(IAzure azure, Region region, TraceWriter log = null)
        {
            // Retrieve appropriate Network Watcher, or create one
            INetworkWatcher networkWatcher = null;
            IEnumerable<INetworkWatcher> networkWatcherList = azure.NetworkWatchers.List();
            if (networkWatcherList != null && networkWatcherList.Count() > 0)
            {
                log.Verbose($"Network Watchers found in subscription - checking if any are in region {region.Name}");
                try
                {
                    networkWatcher = azure.NetworkWatchers.List().First(x => x.Region == region);
                }
                catch (Exception)
                {
                    log.Info($"No network watchers found in region {region.Name}");
                }
            }
            else
            {
                log.Verbose("No Network Watchers found in subscription.");
            }
            if (networkWatcher == null)
            {
                try
                {
                    string networkWatcherRGName = "NetworkWatcherRG";
                    log.Info($"No Network Watcher exists in region {region.Name}. Will attempt to create in ResourceGroup {networkWatcherRGName}");
                    // Create Resource Group for Network Watcher if Network Watcher does not exist
                    IResourceGroup networkWatcherRG = azure.ResourceGroups.GetByName(networkWatcherRGName);
                    if (networkWatcherRG == null)
                    {
                        Region targetNetworkWatcherRGRegion = region;
                        log.Info($"Resource Group {networkWatcherRGName} does not exist. Creating it in region: {targetNetworkWatcherRGRegion.Name}. Note - the region of the Network Watcher's Resource Group does not have to match the region of the Network Watcher.");
                        networkWatcherRG = await azure.ResourceGroups
                          .Define(networkWatcherRGName)
                          .WithRegion(targetNetworkWatcherRGRegion)
                          .CreateAsync();
                        log.Info("Created Resource Group");
                    }

                    string networkWatcherName = "NetworkWatcher_" + region.Name.ToString().ToLower();
                    log.Info($"Creating the network watcher {networkWatcherName} in resource group {networkWatcherRG.Name}");
                    networkWatcher = await azure.NetworkWatchers.Define(networkWatcherName).WithRegion(region).WithExistingResourceGroup(networkWatcherRG).CreateAsync();
                    log.Info($"Network Watcher created successfully");
                }
                catch (Exception ex)
                {
                    log?.Error($"Unable to create ResourceGroup or Network Watcher: {ex}. Exiting.");
                    throw ex;
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
