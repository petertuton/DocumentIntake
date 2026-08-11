@description('Name of the Logic App.')
param name string

@description('Azure region.')
param location string

@description('Resource tags.')
param tags object = {}

@description('Storage account that receives the attachments.')
param storageAccountName string

@description('Container that receives the attachments.')
param inboxContainerName string = 'inbox'

@description('Mailbox folder to monitor.')
param mailboxFolder string = 'Inbox'

@description('How often the mailbox is polled, in minutes.')
param pollIntervalMinutes int = 1

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageAccountName
}

// The Office 365 connection is created unauthorized. Complete the OAuth consent once,
// post-deployment, in the portal: Logic App > API connections > Authorize.
resource office365Connection 'Microsoft.Web/connections@2016-06-01' = {
  name: 'office365-${name}'
  location: location
  tags: tags
  properties: {
    displayName: 'Office 365 Outlook (document intake)'
    api: {
      id: subscriptionResourceId(
        'Microsoft.Web/locations/managedApis',
        location,
        'office365'
      )
    }
  }
}

resource workflow 'Microsoft.Logic/workflows@2019-05-01' = {
  name: name
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    state: 'Enabled'
    definition: {
      '$schema': 'https://schema.management.azure.com/providers/Microsoft.Logic/schemas/2016-06-01/workflowdefinition.json#'
      contentVersion: '1.0.0.0'
      parameters: {
        '$connections': {
          defaultValue: {}
          type: 'Object'
        }
      }
      triggers: {
        When_a_new_email_arrives: {
          type: 'ApiConnection'
          recurrence: {
            frequency: 'Minute'
            interval: pollIntervalMinutes
          }
          inputs: {
            host: {
              connection: {
                name: '@parameters(\'$connections\')[\'office365\'][\'connectionId\']'
              }
            }
            method: 'get'
            path: '/v3/Mail/OnNewEmail'
            queries: {
              folderPath: mailboxFolder
              importance: 'Any'
              fetchOnlyWithAttachment: true
              includeAttachments: true
            }
          }
          splitOn: '@triggerBody()?[\'value\']'
        }
      }
      actions: {
        For_each_attachment: {
          type: 'Foreach'
          foreach: '@triggerBody()?[\'attachments\']'
          runtimeConfiguration: {
            concurrency: {
              repetitions: 1
            }
          }
          actions: {
            Upload_attachment_to_inbox: {
              type: 'Http'
              inputs: {
                method: 'PUT'
                // Blob name is prefixed with the message id so retries overwrite the
                // same blob rather than creating duplicates, and provenance is preserved.
                uri: '@{concat(\'${storage.properties.primaryEndpoints.blob}${inboxContainerName}/\', encodeUriComponent(concat(triggerBody()?[\'id\'], \'_\', items(\'For_each_attachment\')?[\'name\'])))}'
                headers: {
                  'x-ms-blob-type': 'BlockBlob'
                  'x-ms-version': '2023-11-03'
                  'x-ms-meta-messageid': '@{triggerBody()?[\'id\']}'
                  'x-ms-meta-originalfilename': '@{items(\'For_each_attachment\')?[\'name\']}'
                  'Content-Type': '@{items(\'For_each_attachment\')?[\'contentType\']}'
                }
                body: '@base64ToBinary(items(\'For_each_attachment\')?[\'contentBytes\'])'
                authentication: {
                  type: 'ManagedServiceIdentity'
                  audience: 'https://storage.azure.com/'
                }
              }
            }
          }
        }
      }
      outputs: {}
    }
    parameters: {
      '$connections': {
        value: {
          office365: {
            connectionId: office365Connection.id
            connectionName: office365Connection.name
            id: subscriptionResourceId(
              'Microsoft.Web/locations/managedApis',
              location,
              'office365'
            )
          }
        }
      }
    }
  }
}

output name string = workflow.name
output principalId string = workflow.identity.principalId
output office365ConnectionName string = office365Connection.name
