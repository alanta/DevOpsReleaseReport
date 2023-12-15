# Release report

Renders a page with releases that are waiting to go to production.

## Authentication 

The API relies on a Personal Access Token (PAT) with the following rights:

* Build: Read
* Code: Read
* Environment: Read & manage
* Release: Read
* Work Items: Read

## Local development

Make sure you set the User Secrets for the Api project:

```json
  "AzureDevOps": {
    "OrganizationUrl": "https://dev.azure.com/myorg",
    "AccessToken": "???",
    "ProjectName": "MyProject"
  }
```

### Other pre-requisits:

* NodeJS
* [Static WebApp CLI](https://azure.github.io/static-web-apps-cli/), install using:

```powershell
npm install -g @azure/static-web-apps-cli
```

This will install Azure Function Core Tools if needed.

### Run the project

* Launch the solution from Visual Studio
* Run the Static WebApp emulator:

```powershell
swa start http://localhost:5000 --api-location http://localhost:7071
```