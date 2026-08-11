# Manual setup

Two things cannot be automated by `azd up`. Do both after the first provision.

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

Send a test email with an attachment to the mailbox and check that it lands:

```powershell
az storage blob list --account-name (azd env get-value AZURE_STORAGE_ACCOUNT_NAME) `
  --container-name inbox --auth-mode login -o table
```

> The mailbox account needs a licence that includes Exchange Online, and the connection must be
> re-authorized if the account's password changes in a way that revokes the token, or if consent
> is withdrawn.

## 2. Dataverse authentication

This decision was deliberately deferred; `IDataverseClient` isolates it. `Dataverse__Enabled`
defaults to `false`, which logs the payload instead of posting, so the rest of the pipeline is
fully exercisable before you choose.

### Option A — managed identity (preferred)

Dataverse can accept the Function App's system-assigned identity as an application user. This
keeps the "no secrets" property of the rest of the solution.

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
                Dataverse__EntitySetName=new_documentintakes
   ```

`ManagedIdentityAuthHandler` already requests the `{EnvironmentUrl}/.default` scope, so no code
change is required for this option.

### Option B — app registration with a client secret

If managed identity is not viable, register an app, create a secret, add it as an application
user in Dataverse the same way, and replace `ManagedIdentityAuthHandler` on the `dataverse`
HttpClient in `Program.cs` with a client-credentials handler. Store the secret in Key Vault and
reference it from app settings — do not put it in `local.settings.json` or commit it.

Only `Program.cs` and the handler change; `DataverseClient` itself is auth-agnostic.

## 3. Verify end to end

```powershell
# 1. send an email with the known form attached
# 2. watch it move through the containers
$acct = azd env get-value AZURE_STORAGE_ACCOUNT_NAME
foreach ($c in 'inbox','processing','completed','ignored','failed') {
  $n = az storage blob list --account-name $acct --container-name $c --auth-mode login --query "length(@)" -o tsv
  Write-Host "$c : $n"
}
```

A healthy run ends with the file in `completed` and, once Dataverse is enabled, a new record
whose `new_completedbloburl` matches that blob.

Or use the scripted version, which uploads a file and waits for it to reach a terminal container:

```powershell
./scripts/smoke-test.ps1 -File ./path/to/known-form.pdf
```

## Target Dataverse columns

The table must expose the envelope columns plus one column per mapped field:

| Column | Type | Notes |
| ------ | ---- | ----- |
| `new_formtype` | Text | Classifier category. |
| `new_sourceblobname` | Text | Original blob name. |
| `new_completedbloburl` | Text | URL in the `completed` container. |
| `new_processedutc` | Text or DateTime | ISO 8601, UTC. |
| `new_classificationconfidence` | Decimal | 0–1. |
| `new_extractionjson` | Multiline text | Full extraction: value, confidence, bounding box per field. |
| one per entry in `field-map.json` | Text | Scalar value of each extracted field. |

Make `new_extractionjson` generously sized — it holds coordinates for every field.
