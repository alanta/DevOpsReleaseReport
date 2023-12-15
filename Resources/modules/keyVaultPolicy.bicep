@description('The resouce name of the keyvault')
param keyvaultName string

@description('The object id of the identity that needs access to KeyVault')
param objectId string

@description('The tenant in which the identity is defined.')
param tenantId string

resource keyvault 'Microsoft.KeyVault/vaults@2022-07-01' existing = {
  name: keyvaultName
}

resource readSecrets 'Microsoft.KeyVault/vaults/accessPolicies@2022-07-01' = {
  name: 'add'
  parent: keyvault
  properties: {
    accessPolicies: [
      {
        objectId: objectId
        permissions: {
          certificates: []
          keys: []
          secrets: [
            'get'
          ]
          storage: []
        }
        tenantId: tenantId
      }
    ]
  }
}
