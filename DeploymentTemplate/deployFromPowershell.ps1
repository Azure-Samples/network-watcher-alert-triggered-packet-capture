#TODO: Convert to params, and extract all likely params
Param(
    [string] $ResourceGroupLocation = 'southcentralus',
    [string] $targetRGName = "PCAPSample",
    [string] $targetRegion = "northcentralus",
    [string] $appName = "PCAPSampleApp",
    [string] $githubrepo = "https://github.com/Azure-Samples/network-watcher-alert-triggered-packet-capture",
    [string] $githubRepoBranch = "master",
    [string] $armTemplateURI = "https://raw.githubusercontent.com/Azure-Samples/network-watcher-alert-triggered-packet-capture/master/DeploymentTemplate/azureDeploy.json",
    [string] $VMSize = "Standard_A1_v2"
)
Write-Host "Please authenticate using credentials that are capable of creating a Service Principal with 'Owner' permissions in its subscription"
$curLogin = Login-AzureRmAccount

$subscriptionID = $curLogin.Context.Subscription.Id
$myUniquifier = Get-Random
$fakeURI = "http://pcapsample.microsoftdemo.com" + $myUniquifier
$appDisplayName = "PCAPSample" + $myUniquifier

# Get the password as securestring and convert to plaintext, as we'll need to pass the password to the ARM template as a param, and we can't change the ARM parameter to type SecureString due to AzurePortal integration
$SecurePassword = "Empty"
$pwdPrompt = "Please enter a strong password at least 12 characters in length and remember it. Be aware that this script will convert it to a plain password for use in the ARM template."
Write-Host $pwdPrompt
while($SecurePassword.Length -le 12)
{
    $SecurePassword = Read-Host -AsSecureString -prompt $pwdPrompt
}
$BSTR = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecurePassword) 
$PlainPassword = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($BSTR)

$newApp = New-AzureRmADApplication -DisplayName $appDisplayName -IdentifierUris @($fakeURI)
$newSP = New-AzureRMADServicePrincipal -ApplicationId $newApp.ApplicationId -Password $SecurePassword
Write-Host "Wait 60 seconds for Service Principal population..."
Start-Sleep 60
$newRoleAssignment = New-AzureRmRoleAssignment -ObjectId $newSP.Id -RoleDefinitionName "Owner" -Scope ("/subscriptions/{0}" -f $subscriptionID)

Write-Host ("TenantID: {0}" -f $curLogin.Context.Tenant.Id)
Write-Host ("Client Id: {0}" -f $newSP.ApplicationId)
Write-Host ("Client Secret: The password you entered.")

# Now, deploy


$AlertEmailParam = Read-Host -Prompt "Please enter an email address to receive the Sample VM's alert"
# File based parameters don't always play nicely with object params, so use object params only
$JSONFile = Get-Content   ".\azureDeploy.parameters.json" | ConvertFrom-Json
$parameterHash = @{}
$JSONFile.parameters | get-member -MemberType NoteProperty | Where-Object{ -not [string]::IsNullOrEmpty($JSONFile.parameters."$($_.name)")} | ForEach-Object {$parameterHash.add($_.name,$JSONFile.parameters."$($_.name)".value)}

$parameterHash["clientId"] =  $newSP.ApplicationId
$parameterHash["ClientKey"] =  $PlainPassword.ToString()
$parameterHash["TenantId"] =  $curLogin.Context.Tenant.Id
$parameterHash["VMPassword"] = $PlainPassword.ToString()
$parameterHash["AlertEmail"] = $AlertEmailParam.ToString()
$parameterHash

Write-Host "Ensuring Resource Group...."
$targetRG = New-AzureRmResourceGroup -Name $targetRGName -Location $targetRegion -Force
Write-Host "Deploying ARM Template...."
New-AzureRmResourceGroupDeployment -Name "PCAPSampleDeployment" -ResourceGroupName $targetRGName  -TemplateParameterObject $parameterHash -TemplateUri $githubrepo
Write-Host "Deployed ARM Template."
