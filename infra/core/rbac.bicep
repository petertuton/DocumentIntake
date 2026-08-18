@description('Storage account that hosts the document containers.')
param storageAccountName string

@description('Azure AI Services (Foundry) account name.')
param foundryAccountName string

@description('Principal id of the Function App managed identity.')
param functionAppPrincipalId string

@description('Principal id of the Logic App managed identity.')
param logicAppPrincipalId string

@description('Principal id of the jump box VM managed identity. Leave empty when the jump box is not deployed.')
param jumpboxPrincipalId string = ''

var storageBlobDataContributor = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
)
var storageBlobDataReader = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1'
)
var storageQueueDataContributor = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
)
var storageTableDataContributor = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
)
var cognitiveServicesUser = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'a97b65f3-24c7-4388-baec-2e87135dc908'
)

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageAccountName
}

resource foundry 'Microsoft.CognitiveServices/accounts@2024-10-01' existing = {
  name: foundryAccountName
}

// Function App: read/write documents, plus the queues and tables Durable Functions needs.
resource fnBlob 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, functionAppPrincipalId, storageBlobDataContributor)
  properties: {
    roleDefinitionId: storageBlobDataContributor
    principalId: functionAppPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource fnQueue 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, functionAppPrincipalId, storageQueueDataContributor)
  properties: {
    roleDefinitionId: storageQueueDataContributor
    principalId: functionAppPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource fnTable 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, functionAppPrincipalId, storageTableDataContributor)
  properties: {
    roleDefinitionId: storageTableDataContributor
    principalId: functionAppPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// Function App: call Content Understanding with Entra auth.
resource fnFoundry 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: foundry
  name: guid(foundry.id, functionAppPrincipalId, cognitiveServicesUser)
  properties: {
    roleDefinitionId: cognitiveServicesUser
    principalId: functionAppPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// Foundry reads the source document straight from the blob url supplied by the Function.
resource foundryBlob 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, foundry.id, storageBlobDataReader)
  properties: {
    roleDefinitionId: storageBlobDataReader
    principalId: foundry.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Logic App: write email attachments into the inbox container.
resource logicBlob 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, logicAppPrincipalId, storageBlobDataContributor)
  properties: {
    roleDefinitionId: storageBlobDataContributor
    principalId: logicAppPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// Jump box VM: lets az storage commands run over the private endpoints for manual verification.
resource jumpboxBlob 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(jumpboxPrincipalId)) {
  scope: storage
  name: guid(storage.id, jumpboxPrincipalId, storageBlobDataContributor)
  properties: {
    roleDefinitionId: storageBlobDataContributor
    principalId: jumpboxPrincipalId
    principalType: 'ServicePrincipal'
  }
}
