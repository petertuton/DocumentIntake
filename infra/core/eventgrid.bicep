@description('Storage account that raises BlobCreated events.')
param storageAccountName string

@description('Azure region.')
param location string

@description('Resource tags.')
param tags object = {}

@description('Function App that hosts the Event Grid triggered function.')
param functionAppName string

@description('Name of the Event Grid triggered function.')
param functionName string = 'BlobCreatedTrigger'

@description('Container whose blobs start the orchestration.')
param inboxContainerName string = 'inbox'

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageAccountName
}

resource functionApp 'Microsoft.Web/sites@2023-12-01' existing = {
  name: functionAppName
}

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

resource subscription 'Microsoft.EventGrid/systemTopics/eventSubscriptions@2024-06-01-preview' = {
  parent: systemTopic
  name: 'inbox-blob-created'
  properties: {
    destination: {
      endpointType: 'AzureFunction'
      properties: {
        resourceId: '${functionApp.id}/functions/${functionName}'
        maxEventsPerBatch: 1
        preferredBatchSizeInKilobytes: 64
      }
    }
    filter: {
      includedEventTypes: [
        'Microsoft.Storage.BlobCreated'
      ]
      // Only blobs landing in the inbox container start an orchestration.
      subjectBeginsWith: '/blobServices/default/containers/${inboxContainerName}/blobs/'
      enableAdvancedFilteringOnArrays: true
    }
    retryPolicy: {
      maxDeliveryAttempts: 10
      eventTimeToLiveInMinutes: 1440
    }
  }
}

output systemTopicName string = systemTopic.name
