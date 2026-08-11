@description('Name of the Azure AI Services (Foundry) account used for Content Understanding.')
param name string

@description('Azure region. Content Understanding is only available in a subset of regions.')
param location string

@description('Resource tags.')
param tags object = {}

resource account 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: name
  location: location
  tags: tags
  kind: 'AIServices'
  sku: {
    name: 'S0'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    customSubDomainName: name
    publicNetworkAccess: 'Enabled'
    // Entra-only auth: the Function App calls this with its managed identity.
    disableLocalAuth: true
    networkAcls: {
      defaultAction: 'Allow'
    }
  }
}

output id string = account.id
output name string = account.name
output endpoint string = account.properties.endpoint
