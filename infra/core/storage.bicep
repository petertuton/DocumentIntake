@description('Name of the storage account.')
param name string

@description('Azure region.')
param location string

@description('Resource tags.')
param tags object = {}

@description('Blob containers to create for the document lifecycle.')
param containerNames array = [
  'inbox'
  'processing'
  'ignored'
  'completed'
  'failed'
]

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    // Shared keys stay enabled because the Functions host still needs them for the
    // Flex Consumption deployment container; all application access uses managed identity.
    allowSharedKeyAccess: true
    publicNetworkAccess: 'Disabled'
    networkAcls: {
      defaultAction: 'Deny'
      // Event Grid must call the storage control plane (listAccountSas) to configure the
      // BlobCreated event subscription; this requires bypassing the firewall for trusted
      // Microsoft services even though public network access stays disabled.
      // See https://aka.ms/storageevents.
      bypass: 'AzureServices'
    }
  }
}

resource blobServices 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    deleteRetentionPolicy: {
      enabled: true
      days: 7
    }
  }
}

resource containers 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = [
  for containerName in containerNames: {
    parent: blobServices
    name: containerName
    properties: {
      publicAccess: 'None'
    }
  }
]

@description('Container used by Flex Consumption to stage the deployment package.')
resource deploymentContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobServices
  name: 'deploymentpackage'
  properties: {
    publicAccess: 'None'
  }
}

output id string = storage.id
output name string = storage.name
output blobEndpoint string = storage.properties.primaryEndpoints.blob
output deploymentContainerName string = deploymentContainer.name
