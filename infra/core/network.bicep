@description('Name of the storage account reached through private endpoints.')
param storageAccountName string

@description('Azure region.')
param location string

@description('Resource tags.')
param tags object = {}

@description('Adds an AzureBastionSubnet and a jump box subnet so private-endpoint-only storage can still be reached with az cli.')
param deployJumpbox bool = false

var bastionSubnetPrefix = '10.0.3.0/26'
var jumpboxSubnetPrefix = '10.0.4.0/27'

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageAccountName
}

// Bastion itself is reached over the public internet on 443; the jump box has no public IP and
// only accepts SSH from the Bastion subnet.
resource jumpboxNsg 'Microsoft.Network/networkSecurityGroups@2024-05-01' = if (deployJumpbox) {
  name: 'nsg-jumpbox-${storageAccountName}'
  location: location
  tags: tags
  properties: {
    securityRules: [
      {
        name: 'AllowSshFromBastionSubnet'
        properties: {
          priority: 100
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourceAddressPrefix: bastionSubnetPrefix
          sourcePortRange: '*'
          destinationAddressPrefix: '*'
          destinationPortRange: '22'
        }
      }
      {
        name: 'DenyAllOtherInbound'
        properties: {
          priority: 4096
          direction: 'Inbound'
          access: 'Deny'
          protocol: '*'
          sourceAddressPrefix: '*'
          sourcePortRange: '*'
          destinationAddressPrefix: '*'
          destinationPortRange: '*'
        }
      }
    ]
  }
}

var baseSubnets = [
  {
    name: 'function-integration'
    properties: {
      addressPrefix: '10.0.1.0/24'
      delegations: [
        {
          name: 'function-app'
          properties: {
            serviceName: 'Microsoft.App/environments'
          }
        }
      ]
    }
  }
  {
    name: 'private-endpoints'
    properties: {
      addressPrefix: '10.0.2.0/24'
      privateEndpointNetworkPolicies: 'Disabled'
    }
  }
]

var jumpboxSubnets = deployJumpbox
  ? [
      {
        name: 'AzureBastionSubnet'
        properties: {
          addressPrefix: bastionSubnetPrefix
        }
      }
      {
        name: 'jumpbox'
        properties: {
          addressPrefix: jumpboxSubnetPrefix
          networkSecurityGroup: {
            id: jumpboxNsg.id
          }
        }
      }
    ]
  : []

resource vnet 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: 'vnet-${storageAccountName}'
  location: location
  tags: tags
  properties: {
    addressSpace: {
      addressPrefixes: [
        '10.0.0.0/16'
      ]
    }
    subnets: concat(baseSubnets, jumpboxSubnets)
  }
}

resource functionSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-05-01' existing = {
  parent: vnet
  name: 'function-integration'
}

resource privateEndpointSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-05-01' existing = {
  parent: vnet
  name: 'private-endpoints'
}

var storageDnsSuffix = environment().suffixes.storage

// Durable Functions uses blob (leases/large payloads), queue (control and work-item
// queues), and table (instances/history). Flex Consumption deploys from a blob container,
// so no Azure Files content share is needed.
var storageSubresources = [
  {
    name: 'blob'
    zone: 'privatelink.blob.${storageDnsSuffix}'
  }
  {
    name: 'queue'
    zone: 'privatelink.queue.${storageDnsSuffix}'
  }
  {
    name: 'table'
    zone: 'privatelink.table.${storageDnsSuffix}'
  }
]

resource privateDnsZones 'Microsoft.Network/privateDnsZones@2024-06-01' = [
  for subresource in storageSubresources: {
    name: subresource.zone
    location: 'global'
    tags: tags
  }
]

resource dnsVnetLinks 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = [
  for (subresource, index) in storageSubresources: {
    parent: privateDnsZones[index]
    name: 'link-${uniqueString(vnet.id, subresource.zone)}'
    location: 'global'
    properties: {
      registrationEnabled: false
      virtualNetwork: {
        id: vnet.id
      }
    }
  }
]

resource privateEndpoints 'Microsoft.Network/privateEndpoints@2024-05-01' = [
  for subresource in storageSubresources: {
    name: 'pe-${storageAccountName}-${subresource.name}'
    location: location
    tags: tags
    properties: {
      subnet: {
        id: privateEndpointSubnet.id
      }
      privateLinkServiceConnections: [
        {
          name: 'storage-${subresource.name}'
          properties: {
            privateLinkServiceId: storage.id
            groupIds: [
              subresource.name
            ]
          }
        }
      ]
    }
  }
]

resource privateDnsZoneGroups 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = [
  for (subresource, index) in storageSubresources: {
    parent: privateEndpoints[index]
    name: 'default'
    properties: {
      privateDnsZoneConfigs: [
        {
          name: subresource.name
          properties: {
            privateDnsZoneId: privateDnsZones[index].id
          }
        }
      ]
    }
  }
]

output functionSubnetId string = functionSubnet.id
output vnetId string = vnet.id
output bastionSubnetId string = deployJumpbox
  ? resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'AzureBastionSubnet')
  : ''
output jumpboxSubnetId string = deployJumpbox
  ? resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'jumpbox')
  : ''
