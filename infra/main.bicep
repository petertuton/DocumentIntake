targetScope = 'subscription'

@minLength(1)
@maxLength(64)
@description('Name of the environment; used to derive resource names.')
param environmentName string

@minLength(1)
@description('Primary location for all resources.')
param location string

@description('Location for the Azure AI Services account. Content Understanding is only available in selected regions.')
@allowed([
  'westus'
  'swedencentral'
  'australiaeast'
])
param foundryLocation string = 'westus'

@description('Content Understanding API version.')
param contentUnderstandingApiVersion string = '2025-11-01'

@description('Classifier id to register in Content Understanding.')
param classifierId string = 'document_intake_classifier'

@description('Analyzer id to register in Content Understanding.')
param analyzerId string = 'document_intake_form_analyzer'

@description('Mailbox folder monitored by the Logic App.')
param mailboxFolder string = 'Inbox'

@description('Dataverse environment url. Leave empty until an auth strategy is chosen.')
param dataverseEnvironmentUrl string = ''

@description('Dataverse entity set that receives the mapped form.')
param dataverseEntitySetName string = ''

@description('Enables the Dataverse step. Keep false until the application user exists.')
param dataverseEnabled bool = false

@description('Deploys an Azure Bastion + jump box VM so az storage commands can reach the private-endpoint-only storage account.')
param deployJumpbox bool = false

@description('Admin username for the jump box VM.')
param jumpboxAdminUsername string = 'azureuser'

@description('SSH public key authorized for the jump box admin user. Required when deployJumpbox is true.')
@secure()
param jumpboxAdminSshPublicKey string = ''

var resourceToken = toLower(uniqueString(subscription().id, environmentName, location))
var tags = {
  'azd-env-name': environmentName
  workload: 'document-intake'
}

resource rg 'Microsoft.Resources/resourceGroups@2021-04-01' = {
  name: 'rg-${environmentName}'
  location: location
  tags: tags
}

module monitoring 'core/monitoring.bicep' = {
  name: 'monitoring'
  scope: rg
  params: {
    name: resourceToken
    location: location
    tags: tags
  }
}

module storage 'core/storage.bicep' = {
  name: 'storage'
  scope: rg
  params: {
    name: 'st${resourceToken}'
    location: location
    tags: tags
  }
}

module network 'core/network.bicep' = {
  name: 'network'
  scope: rg
  params: {
    storageAccountName: storage.outputs.name
    location: location
    tags: tags
    deployJumpbox: deployJumpbox
  }
}

module jumpbox 'core/jumpbox.bicep' = if (deployJumpbox) {
  name: 'jumpbox'
  scope: rg
  params: {
    name: resourceToken
    location: location
    tags: tags
    jumpboxSubnetId: network.outputs.jumpboxSubnetId
    bastionSubnetId: network.outputs.bastionSubnetId
    adminUsername: jumpboxAdminUsername
    adminSshPublicKey: jumpboxAdminSshPublicKey
  }
}

module foundry 'core/foundry.bicep' = {
  name: 'foundry'
  scope: rg
  params: {
    name: 'ais-${resourceToken}'
    projectName: 'document-intake-${resourceToken}'
    location: foundryLocation
    tags: tags
  }
}

module functionApp 'core/functionapp.bicep' = {
  name: 'functionapp'
  scope: rg
  params: {
    name: 'func-${resourceToken}'
    location: location
    tags: tags
    storageAccountName: storage.outputs.name
    deploymentContainerName: storage.outputs.deploymentContainerName
    appInsightsConnectionString: monitoring.outputs.appInsightsConnectionString
    contentUnderstandingEndpoint: foundry.outputs.endpoint
    foundryProjectEndpoint: foundry.outputs.projectEndpoint
    contentUnderstandingApiVersion: contentUnderstandingApiVersion
    classifierId: classifierId
    analyzerId: analyzerId
    dataverseEnvironmentUrl: dataverseEnvironmentUrl
    dataverseEntitySetName: dataverseEntitySetName
    dataverseEnabled: dataverseEnabled
    virtualNetworkSubnetId: network.outputs.functionSubnetId
  }
}

module logicApp 'core/logicapp.bicep' = {
  name: 'logicapp'
  scope: rg
  params: {
    name: 'logic-${resourceToken}'
    location: location
    tags: tags
    storageAccountName: storage.outputs.name
    mailboxFolder: mailboxFolder
  }
}

module rbac 'core/rbac.bicep' = {
  name: 'rbac'
  scope: rg
  params: {
    storageAccountName: storage.outputs.name
    foundryAccountName: foundry.outputs.name
    foundryProjectPrincipalId: foundry.outputs.projectPrincipalId
    functionAppPrincipalId: functionApp.outputs.principalId
    logicAppPrincipalId: logicApp.outputs.principalId
    jumpboxPrincipalId: deployJumpbox ? jumpbox.outputs.jumpboxPrincipalId : ''
  }
}

module eventGrid 'core/eventgrid.bicep' = {
  name: 'eventgrid'
  scope: rg
  params: {
    storageAccountName: storage.outputs.name
    location: location
    tags: tags
  }
  dependsOn: [
    rbac
  ]
}

output AZURE_LOCATION string = location
output AZURE_RESOURCE_GROUP string = rg.name
output AZURE_STORAGE_ACCOUNT_NAME string = storage.outputs.name
output AZURE_STORAGE_BLOB_ENDPOINT string = storage.outputs.blobEndpoint
output AZURE_FUNCTION_APP_NAME string = functionApp.outputs.name
output AZURE_FOUNDRY_ACCOUNT_NAME string = foundry.outputs.name
output AZURE_AI_PROJECT_ID string = foundry.outputs.projectId
output AZURE_AI_PROJECT_NAME string = foundry.outputs.projectName
output AZURE_AI_PROJECT_ENDPOINT string = foundry.outputs.projectEndpoint
output CONTENT_UNDERSTANDING_ENDPOINT string = foundry.outputs.endpoint
output CONTENT_UNDERSTANDING_API_VERSION string = contentUnderstandingApiVersion
output CONTENT_UNDERSTANDING_CLASSIFIER_ID string = classifierId
output CONTENT_UNDERSTANDING_ANALYZER_ID string = analyzerId
output CONTENT_UNDERSTANDING_COMPLETION_DEPLOYMENT string = foundry.outputs.completionDeploymentName
output CONTENT_UNDERSTANDING_EMBEDDING_DEPLOYMENT string = foundry.outputs.embeddingDeploymentName
output LOGIC_APP_NAME string = logicApp.outputs.name
output LOGIC_APP_O365_CONNECTION_NAME string = logicApp.outputs.office365ConnectionName
output APPLICATIONINSIGHTS_NAME string = monitoring.outputs.appInsightsName
output BASTION_NAME string = deployJumpbox ? jumpbox.outputs.bastionName : ''
output JUMPBOX_VM_NAME string = deployJumpbox ? jumpbox.outputs.vmName : ''
