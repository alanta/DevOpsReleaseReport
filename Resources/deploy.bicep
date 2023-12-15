@description('The name of the resource')
param name string
@description('The DevOps organization url')
param organizationUrl string
@description('The DevOps project name')
param projectName string
@secure()

param repositoryUrl string // 'https://dev.azure.com/<org>/<project>/_git/<repo>'
param branch string = 'main'

param resourceTags object = {}

param deploymentTimestamp string = utcNow('yyMMddhhmmss')
@description('The location of resources.')
param location string = resourceGroup().location

var integration = {
  resourceGroup: 'integration'
  keyvault: 'shared-kv'
  appinsights: 'shared-appinsights'
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: integration.appinsights
  scope: resourceGroup(integration.resourceGroup)
}

resource managedIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2021-09-30-preview' = {
  name: 'umi-${name}'
  location: location
}

module keyvaultAccess 'modules/keyVaultPolicy.bicep' = {
  name: 'kv-${name}-${deploymentTimestamp}'
  scope:  resourceGroup(subscription().subscriptionId, integration.resourceGroup)
  params: {
    keyvaultName: integration.keyvault
    objectId: managedIdentity.properties.principalId
    tenantId: subscription().tenantId
  }
}

resource staticWebApp 'Microsoft.Web/staticSites@2022-03-01' = {
  name: name
  location: location
  tags: resourceTags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${managedIdentity.id}': {}
    }  
  }
  properties: {
    
    repositoryUrl: repositoryUrl
    branch: branch
    buildProperties: {
      appLocation: '/Client'
      apiLocation: '/Api'
    }
  }
  sku: {
    name: 'Standard'
  }
}

resource appsettings 'Microsoft.Web/staticSites/config@2021-01-15' = {
  parent: staticWebApp
  name: 'appsettings'
  properties: {
    AZURE_CLIENT_ID: '@Microsoft.KeyVault(VaultName=${integration.keyvault};SecretName=ReleaseReportClientId)'
    AZURE_CLIENT_SECRET: '@Microsoft.KeyVault(VaultName=${integration.keyvault};SecretName=ReleaseReportClientSecret)'
    APPINSIGHTS_INSTRUMENTATIONKEY: appInsights.properties.InstrumentationKey
    APPLICATIONINSIGHTS_CONNECTION_STRING: appInsights.properties.ConnectionString
    AzureDevOps__OrganizationUrl: organizationUrl
    AzureDevOps__AccessToken: '@Microsoft.KeyVault(VaultName=${integration.keyvault};SecretName=ReleaseReportDevopsToken)'
    AzureDevOps__ProjectName: projectName
  }
}
