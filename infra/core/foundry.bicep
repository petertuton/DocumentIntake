@description('Name of the Azure AI Services (Foundry) account.')
param name string

@description('Name of the Microsoft Foundry project under the account.')
param projectName string

@description('Azure region. Content Understanding is only available in a subset of regions.')
param location string

@description('Resource tags.')
param tags object = {}

resource account 'Microsoft.CognitiveServices/accounts@2025-06-01' = {
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
    allowProjectManagement: true
    customSubDomainName: name
    publicNetworkAccess: 'Enabled'
    // Entra-only auth: the Function App calls this with its managed identity.
    disableLocalAuth: true
    networkAcls: {
      defaultAction: 'Allow'
    }
  }
}

resource project 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' = {
  parent: account
  name: projectName
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    description: 'Microsoft Foundry project for Document Intake.'
    displayName: 'Document Intake'
  }
}

// Content Understanding requires default model deployments before any analyzer using
// generative fields (classification, "generate" methods) can run.
resource completionDeployment 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = {
  parent: account
  name: 'gpt-5-mini'
  sku: {
    name: 'GlobalStandard'
    capacity: 50
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-5-mini'
      version: '2025-08-07'
    }
  }
}

resource embeddingDeployment 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = {
  parent: account
  name: 'text-embedding-3-large'
  sku: {
    name: 'GlobalStandard'
    capacity: 50
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'text-embedding-3-large'
      version: '1'
    }
  }
  dependsOn: [
    completionDeployment
  ]
}

output id string = account.id
output name string = account.name
output endpoint string = account.properties.endpoint
output projectId string = project.id
output projectName string = project.name
output projectPrincipalId string = project.identity.principalId
output projectEndpoint string = 'https://${name}.services.ai.azure.com/api/projects/${project.name}'
output completionDeploymentName string = completionDeployment.name
output embeddingDeploymentName string = embeddingDeployment.name
