@description('Name of the Function App.')
param name string

@description('Azure region.')
param location string

@description('Resource tags.')
param tags object = {}

@description('Storage account used for the Functions runtime, durable state, and documents.')
param storageAccountName string

@description('Container that holds the Flex Consumption deployment package.')
param deploymentContainerName string

@description('Application Insights connection string.')
param appInsightsConnectionString string

@description('Content Understanding (AI Services) endpoint.')
param contentUnderstandingEndpoint string

@description('Content Understanding API version.')
param contentUnderstandingApiVersion string

@description('Classifier id registered in Content Understanding.')
param classifierId string

@description('Analyzer id registered in Content Understanding.')
param analyzerId string

@description('Dataverse environment url. Leave empty until an auth strategy is chosen.')
param dataverseEnvironmentUrl string = ''

@description('Dataverse entity set that receives the mapped form.')
param dataverseEntitySetName string = ''

@description('Enables the Dataverse step. Keep false until the application user exists.')
param dataverseEnabled bool = false

@description('Subnet used for outbound VNet integration to private storage endpoints.')
param virtualNetworkSubnetId string

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageAccountName
}

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: 'plan-${name}'
  location: location
  tags: tags
  sku: {
    tier: 'FlexConsumption'
    name: 'FC1'
  }
  kind: 'functionapp'
  properties: {
    reserved: true
  }
}

resource functionApp 'Microsoft.Web/sites@2023-12-01' = {
  name: name
  location: location
  tags: union(tags, { 'azd-service-name': 'functions' })
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: plan.id
    virtualNetworkSubnetId: virtualNetworkSubnetId
    httpsOnly: true
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storage.properties.primaryEndpoints.blob}${deploymentContainerName}'
          authentication: {
            type: 'SystemAssignedIdentity'
          }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: 40
        instanceMemoryMB: 2048
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '10.0'
      }
    }
    siteConfig: {
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      appSettings: [
        // Identity-based connection: no storage keys in configuration.
        {
          name: 'AzureWebJobsStorage__blobServiceUri'
          value: storage.properties.primaryEndpoints.blob
        }
        {
          name: 'AzureWebJobsStorage__queueServiceUri'
          value: storage.properties.primaryEndpoints.queue
        }
        {
          name: 'AzureWebJobsStorage__tableServiceUri'
          value: storage.properties.primaryEndpoints.table
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsightsConnectionString
        }
        {
          name: 'Storage__BlobServiceUri'
          value: storage.properties.primaryEndpoints.blob
        }
        {
          name: 'ContentUnderstanding__Endpoint'
          value: contentUnderstandingEndpoint
        }
        {
          name: 'ContentUnderstanding__ApiVersion'
          value: contentUnderstandingApiVersion
        }
        {
          name: 'ContentUnderstanding__ClassifierId'
          value: classifierId
        }
        {
          name: 'ContentUnderstanding__AnalyzerId'
          value: analyzerId
        }
        {
          name: 'Dataverse__EnvironmentUrl'
          value: dataverseEnvironmentUrl
        }
        {
          name: 'Dataverse__EntitySetName'
          value: dataverseEntitySetName
        }
        {
          name: 'Dataverse__Enabled'
          value: string(dataverseEnabled)
        }
      ]
    }
  }
}

output id string = functionApp.id
output name string = functionApp.name
output principalId string = functionApp.identity.principalId
output defaultHostName string = functionApp.properties.defaultHostName
