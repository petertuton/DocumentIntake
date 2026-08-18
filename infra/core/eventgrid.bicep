@description('Storage account that raises BlobCreated events.')
param storageAccountName string

@description('Azure region.')
param location string

@description('Resource tags.')
param tags object = {}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageAccountName
}

// The event subscription is created after `azd deploy` by scripts/register-event-subscription.ps1.
// Event Grid validates the AzureFunction endpoint at creation time, which fails while the
// Function App has no deployed code.
resource systemTopic 'Microsoft.EventGrid/systemTopics@2024-06-01-preview' = {
  name: 'evgt-${storageAccountName}'
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    source: storage.id
    topicType: 'Microsoft.Storage.StorageAccounts'
  }
}

output systemTopicName string = systemTopic.name
output systemTopicId string = systemTopic.id
