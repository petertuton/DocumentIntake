# Runbook

> The storage account only accepts private endpoint traffic. Run the `az storage` commands below
> from the jump box described in [manual-setup.md](./manual-setup.md#0-reaching-private-endpoint-only-storage-with-az-cli),
> or from anything else already on the VNet.

## Quick triage

A file in `failed` is the signal that something went wrong. Its `intakeReason` metadata carries
the exception message (truncated to 1000 characters).

```powershell
$acct = azd env get-value AZURE_STORAGE_ACCOUNT_NAME

az storage blob list --account-name $acct --container-name failed --auth-mode login `
  --query "[].{name:name, modified:properties.lastModified}" -o table

az storage blob metadata show --account-name $acct --container-name failed `
  --name "<blob>" --auth-mode login
```

## Finding the orchestration

The instance id is `di-` plus the first 32 hex characters of `SHA256("<blobName>|<etag>")`.
It is easier to search the logs:

```kusto
traces
| where timestamp > ago(1d)
| where message has "<blobName>"
| project timestamp, message, operation_Id
| order by timestamp asc
```

Then pull the full orchestration trail:

```kusto
traces
| where timestamp > ago(1d)
| where message has "di-<hash>"
| order by timestamp asc
```

Durable history also lives in the storage account's `DocumentIntakeHub` tables
(`DocumentIntakeHubInstances`, `DocumentIntakeHubHistory`).

## Common failures

| Symptom | Likely cause | Action |
| ------- | ------------ | ------ |
| Nothing lands in `inbox` | O365 connection not authorized | Authorize it — see [manual-setup.md](manual-setup.md). |
| Blobs land but no orchestration starts | Event Grid subscription unhealthy | Check the system topic's delivery failures in the portal; confirm the Function App is running. |
| `403` from Content Understanding | Function MI missing **Cognitive Services User** | Re-run `azd provision`; RBAC lives in `infra/core/rbac.bicep`. |
| Content Understanding cannot read the blob | Foundry MI missing **Storage Blob Data Contributor**, or the URL is not publicly resolvable | Re-run `azd provision`. Foundry reaches storage over the public endpoint. |
| Everything goes to `ignored` | Classifier not registered, or confidence threshold too high | Run `scripts/register-analyzers.ps1`; check `ContentUnderstanding__MinimumClassificationConfidence`. |
| `Analysis timed out` in `intakeReason` | Document exceeded the 10-minute poll window | Confirm the analyzer completes for that document; raise `DocumentIntakeOrchestrator.PollTimeout` if genuinely needed. |
| `DocumentTooLargeException` | Attachment above the configured size guard | Handle the document out of band. |
| Dataverse `401`/`403` | Auth not configured, or the application user lacks the security role | See [manual-setup.md](manual-setup.md). |
| Payload logged but nothing created in Dataverse | `Dataverse__Enabled` is `false` | Expected until the auth decision is made. Set it to `true`. |

## Replaying a failed blob

Fix the root cause first, then copy the blob back into `inbox`. The Event Grid event fires on
create, so the copy is enough to restart the orchestration — and because the new blob has a new
etag, it gets a fresh instance id rather than colliding with the failed run.

```powershell
$acct = azd env get-value AZURE_STORAGE_ACCOUNT_NAME
$blob = "<blob>"

az storage blob copy start `
  --account-name $acct --auth-mode login `
  --destination-container inbox --destination-blob $blob `
  --source-container failed --source-blob $blob

# once the copy reports success
az storage blob delete --account-name $acct --auth-mode login `
  --container-name failed --name $blob
```

To replay everything in `failed`:

```powershell
$acct = azd env get-value AZURE_STORAGE_ACCOUNT_NAME
az storage blob list --account-name $acct --container-name failed --auth-mode login `
  --query "[].name" -o tsv | ForEach-Object {
    az storage blob copy start --account-name $acct --auth-mode login `
      --destination-container inbox --destination-blob $_ `
      --source-container failed --source-blob $_
  }
```

## Stuck in `processing`

A blob sitting in `processing` with no running orchestration means the host died mid-flight.
Check for a terminal instance in the durable tables; if there is none, move the blob back to
`inbox` using the same copy-then-delete pattern above.

## Health checks

```powershell
# is the app up
az functionapp show -g (azd env get-value AZURE_RESOURCE_GROUP) `
  -n (azd env get-value AZURE_FUNCTION_APP_NAME) --query state -o tsv

# recent exceptions
# (Application Insights -> Logs)
```

```kusto
exceptions
| where timestamp > ago(6h)
| summarize count() by problemId, outerMessage
| order by count_ desc
```

## Rotating the analyzer definition

Editing the analyzer or classifier JSON and re-running `scripts/register-analyzers.ps1` replaces
the definition in place (`PUT`). In-flight operations continue against the old definition; new
submissions pick up the new one. Update `field-map.json` in the same change and redeploy the
Function App so the mapping stays in sync.
