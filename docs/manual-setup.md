# Manual setup

Two things cannot be automated by `azd up`. Do both after the first provision.

The deployment creates a Microsoft Foundry project under the Azure AI Services account. Confirm
the project endpoint after provisioning:

```powershell
azd env get-value AZURE_AI_PROJECT_NAME
azd env get-value AZURE_AI_PROJECT_ENDPOINT
```

Use the project endpoint for project-scoped Foundry APIs. Keep the
`CONTENT_UNDERSTANDING_ENDPOINT` account endpoint for analyzer registration because Content
Understanding is a Foundry Tool endpoint.

## 0. Reaching private-endpoint-only storage with az cli

The storage account has `publicNetworkAccess` disabled, so `az storage` commands used later in this
guide only work from inside the VNet. Deploy an optional Azure Bastion + jump box VM to get there.

If you don't already have an SSH key pair, generate one first (skip this if `~/.ssh/id_ed25519.pub`
already exists):

```powershell
ssh-keygen -t ed25519 -f ~/.ssh/id_ed25519 -C "jumpbox"
```

Leave the passphrase empty or set one; either works for `az network bastion ssh` since it prompts
locally, not over the network.

Azure Bastion needs a Standard SKU public IP, which some subscriptions (trial, CSP, sponsored)
haven't been enabled for. Register the feature once, before the first `DEPLOY_JUMPBOX=true`
provision (propagation can take a few minutes):

```powershell
az feature register --namespace Microsoft.Network --name AllowBringYourOwnPublicIpAddress
az provider register --namespace Microsoft.Network
```

```powershell
azd env set DEPLOY_JUMPBOX true
azd env set JUMPBOX_ADMIN_SSH_PUBLIC_KEY (Get-Content ~/.ssh/id_ed25519.pub -Raw).Trim()
azd provision
```

Connect over Bastion's native client (no open ports on the VM, no public IP). Echo the storage
account name first so you can copy it before the `az storage` commands run inside the VM (`azd`
isn't available there):

```powershell
$rg = azd env get-value AZURE_RESOURCE_GROUP
$bastion = azd env get-value BASTION_NAME
$vm = azd env get-value JUMPBOX_VM_NAME

azd env get-value AZURE_STORAGE_ACCOUNT_NAME

az network bastion ssh -n $bastion -g $rg `
  --target-resource-id (az vm show -g $rg -n $vm --query id -o tsv) `
  --auth-type ssh-key --username azureuser --ssh-key ~/.ssh/id_ed25519
```

From the jump box's shell, log in and run the `az storage` commands in this guide as normal —
paste the account name echoed above into `acct`; the VM's managed identity already has
`Storage Blob Data Contributor` on the account:

```bash
az login --identity
acct=<paste the echoed account name here>
az storage blob list --account-name $acct --container-name inbox --auth-mode login -o table
```

> Leave `DEPLOY_JUMPBOX` unset (or `false`) once you no longer need CLI access; re-run
> `azd provision` to tear the Bastion host, public IP, NSG, and VM back down.

## 1. Authorize the Office 365 Outlook connection

Bicep creates the API connection in an **unauthorized** state — OAuth consent is interactive by
design and cannot be scripted. Until this is done the Logic App trigger never fires.

1. Portal → the resource group → the `office365` API connection.
2. **Edit API connection** → **Authorize**.
3. Sign in as the account that owns the monitored mailbox.
4. **Save**.

Then confirm the workflow is enabled and pointed at the right folder:

```powershell
$rg = azd env get-value AZURE_RESOURCE_GROUP
$la = azd env get-value LOGIC_APP_NAME

az logic workflow show -g $rg -n $la --query "{state:state, trigger:definition.triggers}" -o json
```

Send a test email with an attachment to the mailbox and check that it lands. `azd` only runs on
your local machine, so resolve the storage account name there first:

```powershell
azd env get-value AZURE_STORAGE_ACCOUNT_NAME
```

> Run the `az storage` command below from the jump box (see [0. Reaching private-endpoint-only storage with az cli](#0-reaching-private-endpoint-only-storage-with-az-cli))
> since the storage account only accepts traffic from its private endpoints — paste the account
> name from above into `acct`.

```bash
acct=<paste the echoed account name here>
az storage blob list --account-name $acct --container-name inbox --auth-mode login -o table
```

> The mailbox account needs a licence that includes Exchange Online, and the connection must be
> re-authorized if the account's password changes in a way that revokes the token, or if consent
> is withdrawn.

## 2. Dataverse authentication

This decision was deliberately deferred; `IDataverseClient` isolates it. `Dataverse__Enabled`
defaults to `false`, which logs the payload instead of posting, so the rest of the pipeline is
fully exercisable before you choose.

### Option A: app registration with a client secret (cross-tenant)

Use this mode while the Function App and Dataverse environment are in different Microsoft Entra
tenants. The app registration and the Dataverse application user must belong to the tenant that
owns the Dataverse environment.

1. Ask the Dataverse administrator to create an application registration and Dataverse application
   user, then assign a least-privilege role with **Create** access to the target table.
2. Obtain the Dataverse tenant ID, application (client) ID, and a client secret. Store the secret in
   Key Vault and configure the Function App setting as a Key Vault reference.
3. Set the app settings:

   ```powershell
   az functionapp config appsettings set `
     -g (azd env get-value AZURE_RESOURCE_GROUP) `
     -n (azd env get-value AZURE_FUNCTION_APP_NAME) `
     --settings Dataverse__Enabled=true `
                Dataverse__EnvironmentUrl=https://<org>.crm.dynamics.com `
                Dataverse__EntitySetName=new_documentintakes `
                Dataverse__AuthMode=ClientSecret `
                Dataverse__ClientSecretTenantId=<dataverse-tenant-id> `
                Dataverse__ClientSecretClientId=<application-client-id> `
                Dataverse__ClientSecretValue='@Microsoft.KeyVault(SecretUri=https://<vault>.vault.azure.net/secrets/<secret-name>)'
   ```

Do not add the secret to `local.settings.json` or commit it. When running locally, set
`Dataverse__ClientSecretValue` through user secrets or an untracked `local.settings.json` file.

### Option B: managed identity (same tenant)

Use this mode when the Function App and Dataverse environment belong to the same Microsoft Entra
tenant. It keeps the "no secrets" property of the rest of the solution.

1. Get the Function App's principal id:

   ```powershell
   az functionapp identity show `
     -g (azd env get-value AZURE_RESOURCE_GROUP) `
     -n (azd env get-value AZURE_FUNCTION_APP_NAME) `
     --query principalId -o tsv
   ```

2. Power Platform admin center → your environment → **Settings** → **Users + permissions** →
   **Application users** → **New app user**.
3. Add the identity's app id, pick the business unit, and assign a security role that permits
   **Create** on the target table.
4. Set the app settings:

   ```powershell
   az functionapp config appsettings set `
     -g (azd env get-value AZURE_RESOURCE_GROUP) `
     -n (azd env get-value AZURE_FUNCTION_APP_NAME) `
     --settings Dataverse__Enabled=true `
                Dataverse__EnvironmentUrl=https://<org>.crm.dynamics.com `
                Dataverse__EntitySetName=new_documentintakes `
                Dataverse__AuthMode=ManagedIdentity
   ```

`DataverseClient` already requests the `{EnvironmentUrl}/.default` scope for whichever
environment the document's classification resolves to, so no code change is required for this
option.

### Routing a classification to a different environment or entity set

`Dataverse__EnvironmentUrl`, `Dataverse__EntitySetName`, and (for Option A) the three
`Dataverse__ClientSecret*` settings are the defaults used when a document's classification has no
specific mapping. To send a classification to its own environment, entity set, or app
registration, add an indexed `Dataverse__FormMappings__<index>__*` entry per field you want to
override; anything left unset falls back to the default. `FormMappings` is a list, not a
dictionary keyed by classification, because Azure App Service settings become environment
variables and classification names such as `hipp-application` contain a hyphen, which isn't a
valid environment variable name character. For example, to route `hipp-application` documents to
the `cr417_annotatedpdf` entity set:

```powershell
az functionapp config appsettings set `
  -g (azd env get-value AZURE_RESOURCE_GROUP) `
  -n (azd env get-value AZURE_FUNCTION_APP_NAME) `
  --settings Dataverse__FormMappings__0__Classification=hipp-application `
             Dataverse__FormMappings__0__EntitySetName=cr417_annotatedpdf
```

If `Dataverse__AuthMode` is `ClientSecret`, a mapping's `ClientSecretTenantId`, `ClientSecretClientId`,
and `ClientSecretValue` must be set together (or all left blank to use the defaults) — startup
validation rejects a partial override, since mixing a tenant or client from one app registration
with a secret from another would silently authenticate as the wrong identity.

## 3. Verify end to end

Get the storage account name locally (`azd` isn't available on the jump box):

```powershell
azd env get-value AZURE_STORAGE_ACCOUNT_NAME
```

Then, from the jump box, send an email with the known form attached and watch it move through the
containers (paste the account name from above into `acct`):

```bash
acct=<paste the echoed account name here>
for c in inbox processing completed ignored failed; do
  n=$(az storage blob list --account-name $acct --container-name $c --auth-mode login --query "length(@)" -o tsv)
  echo "$c : $n"
done
```

A healthy run ends with the file in `completed` and, once Dataverse is enabled, a new record
whose `new_completedbloburl` matches that blob.

Or use the scripted version, which uploads a file and waits for it to reach a terminal container:

```powershell
./scripts/smoke-test.ps1 -File ./path/to/hipp-application.pdf
```

## Target Dataverse columns

For this PoC, the table must expose three columns for each mapped field in
`analyzers/contentunderstanding/field-map.json`:

| Column | Type | Notes |
| ------ | ---- | ----- |
| `<field>` | Text | Scalar extracted value, for example `cr417_field1`. |
| `<field>confidence` | Decimal | Confidence from Content Understanding, from 0 to 1. |
| `<field>source` | Text | Bounding box in `D(page,x1,y1,...)` format, or null when unavailable. |

The current mappings are `cr417_field1`, `cr417_field2`, and `cr417_field3`, so the related
confidence and source columns are named `cr417_field1confidence`, `cr417_field1source`, and so
on.
