# Standalone Mode

Upgrade Center supports two integration modes. This setting is independent of the existing BI/Reports/Common product deployment mode.

| Integration mode | Configuration and key source | Browser authentication |
| --- | --- | --- |
| IdpIntegrated (default) | Existing IDP configuration and private-key APIs | Existing IDP/UMS session validation |
| Standalone | Read-only product configuration/key access through filesystem, S3, Azure Blob, or OCI | Upgrade Center's own administrator login and session |

Mode names are case-insensitive. Change the mode through configuration and restart Upgrade Center. An invalid mode stops startup rather than selecting another mode automatically.

## Enable Standalone Mode

Helm supports filesystem/PVC and object-storage Standalone configuration. For Helm, enable it in your existing product values file:

```yaml
upgradeCenter:
  enabled: true
  integrationMode: Standalone
```

For BI/Reports filesystem kubectl manifests, set `integrationMode: "Standalone"` in the Upgrade Center ConfigMap. For non-PVC object-storage configuration, configure and apply `deploy/bold-upgrade-center-object-storage` using `kubectl apply -k`; this overlay removes the product-PVC mount and IDP wait. Common deployment supports Helm only, and its product chart remains PVC-backed. Use an Upgrade Center image built with the latest Standalone object-storage support.

Filesystem deployment templates mount the existing product PVC read-only and manage configuration/key file paths internally. Object-storage templates reuse the existing storage settings and reference credentials through Secrets. No path settings are required in customer values. Keep configuration and encrypted private keys current and from the same installation. Do not create or export a decrypted key.

The application base URL is read automatically from `InternalAppUrls.Idp` in product configuration, preserving any base path. No separate application URL setting is required or used. Missing or invalid configuration URLs block startup with a clear error. The existing version, health-check, Back to application, and Playwright services use this configuration-derived URL.

Supply administrator credentials through a Secret or the platform's protected credential mechanism:

```text
BOLD_ADMIN_USERNAME=<configured administrator username>
BOLD_ADMIN_PASSWORD=<configured administrator password>
```

These existing shared credentials are also used by Playwright. Optional separate Upgrade Center login credentials can be supplied through:

```text
UpgradeCenter__Authentication__Standalone__Username=<Upgrade Center username>
UpgradeCenter__Authentication__Standalone__Password=<Upgrade Center password>
```

Both optional login values should be configured together. Each configured value takes priority over its BOLD_ADMIN counterpart. Separate local login credentials do not replace the application's credentials used by Playwright. Do not put passwords or client secrets in a ConfigMap, URL, or source-controlled settings file.

## Object-Storage Standalone Support

Reuse the product's existing storage settings. There is no additional Upgrade Center storage selector and no storage probing. Helm passes the existing product values; Kubernetes non-PVC overlays reference the storage Secrets already used by the product.

| Product storage type | Configuration/key access |
| --- | --- |
| FileStorage (`BOLD_SERVICES_STORAGETYPE=0` or no object-storage setting) | Existing read-only product PVC mount |
| AzureBlob (`1`) | Existing Azure account, container, connection type, endpoint, and access key |
| AmazonS3 (`3`) | Existing S3 bucket, region, root folder, access key, and secret key |
| OCIObjectStorage (`4`) | Existing OCI bucket, namespace, region, root folder, and S3-compatible credentials |

The Helm `$hasRealPV` check describes configured product PV usage; it does not verify live volume binding or file presence. It controls PVC mounts. The configured product storage type controls where configuration is read. If both object storage and a PVC are configured, Upgrade Center follows the product's object-storage selection rather than assuming configuration is on the PVC.

Templates use the same storage flags and environment-variable order as product deployments. They do not add restrictions on the number of enabled backend sections or require duplicate Upgrade Center connection values. Configure the backend consistently with the running product. Legacy explicit application source settings remain compatible, but new deployment templates do not emit them.

Storage credentials are kept in Secrets, not ConfigMaps. Filesystem/object names stay internal to the application/templates. The existing product HTTP/HTTPS/custom Azure endpoint formats are accepted; prefer HTTPS where possible. Startup still checks that enough information is available to connect and reports sanitized errors for invalid settings or credentials.

The configuration readers only download the product configuration and private-key objects. They never modify those objects or create buckets/containers. Without a product PV, a separate persistence client reads and writes Upgrade Center's own reports and authentication keys in the dedicated location described below. Reusing product credentials does not reduce their existing permissions; grant only the access required where practical.

### Runtime Configuration Freshness

For object storage, configuration and key objects bootstrap a connection to the existing master database. Upgrade Center performs read-only queries against the configured schema/prefix's system-settings table and uses `RuntimeConfigurationJson` when present. Legacy databases without this row continue using the object configuration. Missing tables, insufficient permissions, connectivity failures, empty/duplicate runtime rows, invalid JSON, or decryption failures block loading; they do not silently use an older configuration.

The runtime configuration must identify the same master database, schema, and prefix as the bootstrap configuration. A difference stops loading and requires refreshing the product storage configuration; Upgrade Center does not switch database targets automatically. It never writes runtime settings or performs an IDP configuration migration.

Object versions are checked again after database resolution to reject configuration/key rotation during loading. Valid snapshots are cached for 30 seconds; after expiry, a refresh failure is returned rather than serving stale data. Loading has a 60-second timeout, bounded retries, and a 4 MiB limit per object. Errors do not include object contents, signed URLs, credentials, or raw SDK/database exception details. Do not rotate configuration/key objects during an active upgrade or rollback.

### Upgrade Center Persistence

With a product PV, Upgrade Center keeps its existing filesystem storage for reports and authentication keys. Without a product PV (`BOLD_SERVICES_PV_USED=false`), it reuses the existing S3, Azure Blob, or OCI connection settings for its own persistent data. Helm and the BI/Reports object-storage overlays do not create a dedicated Upgrade Center PV/PVC. This applies to both integration modes; IDP-integrated configuration/authentication still use IDP/UMS.

Upgrade Center stores its data under the product storage root's `app_data/upgrade-center/` prefix, separate from product configuration/private keys. The subfolders are `playwright-reports/` and `data-protection-keys/`. It requires read/list/write access to these locations and delete access to its own reports for retention. Read-only product-configuration credentials alone are not sufficient. Reports follow the existing retained-job count. Authentication key objects are append-only and are not deleted by report cleanup. Use private storage, encryption at rest, and no expiry/lifecycle deletion for the authentication-key prefix.

Storage/key errors do not fall back to ephemeral local storage. Startup checks durable authentication-key access. Configure the product's storage backend before enabling a no-PV deployment; an absent backend is an error. There are no additional Upgrade Center storage values to fill in.

Playwright still uses its separate temporary shared-state PVC to share files between pre-validation, post-validation, and cleanup. Its execution/cleanup behavior and StorageClass requirement are unchanged. Common's product chart remains PVC-backed and Helm-only.

Existing reports/keys on an older dedicated Upgrade Center data PVC are not automatically migrated. Before replacing that deployment, preserve its contents and plan migration to object storage; losing keys invalidates existing sessions. Do not delete existing claims or product data as part of this change.

## Mounted Configuration Requirements

The file provider reads the same product configuration fields as the existing IDP provider:

- InternalAppUrls.Idp, Bi, and Reports/Report/Reporting.
- SqlConfiguration.ConnectionString, ServerType, SchemaName, and Prefix.
- MachineKey.DecryptionKey.
- UseSingleTenantDb, including the supported SystemSettings form.
- InternalAppClientId, InternalAppClientSecret, ShowDataHub, and configured BoldProducts versions where present.

Startup checks require a valid application base URL, encrypted master connection, database server type, decryption key, explicit database mode, readable private-key file, and configured local login credentials. The existing decryptor validates that the file pair can decrypt the master connection before the application starts. This validation does not replace database connectivity, permissions, or product eligibility checks performed by the existing workflow.

Ensure the mounted configuration belongs to the running installation and is current. The filesystem provider retains its existing file-only behavior; it does not query database-backed runtime settings. Object-storage providers perform the runtime lookup described above. Neither calls IDP to reconcile configuration. Do not change the files or database targets during an active upgrade/rollback.

The provider reads files without modifying them. File changes are validated before use; missing, empty, malformed, or mismatched files fail clearly without returning an older cached snapshot or falling back to IDP APIs. Cancellation is propagated. Decryption/JSON errors do not expose file contents or database secrets.

## Login and Logout

Unauthenticated customers are sent to /upgrade-center/login. They enter the configured local administrator credentials. No IDP configuration, private-key, login, or UMS session-validation request is needed for local sign-in.

The local account is an Upgrade Center administrator authorized by the deployment owner. It does not prove the account's current role/password in IDP. Playwright still authenticates against the product using its configured application credentials.

The local session uses a separate encrypted, HttpOnly, SameSite=Strict cookie, with a default absolute lifetime of 30 minutes and no persistent browser login. Production requires HTTPS. SessionMinutes can be configured between 5 and 480. Sign-in/sign-out forms require antiforgery tokens. Login POST requests are limited to five per client IP per minute in each application instance.

Credential values are loaded at startup. Restart/redeploy after credential changes. Changing either configured credential invalidates old local sessions on the restarted instance. Preserve Upgrade Center's own data-protection key directory across restarts to support valid existing sessions; share the keys if multiple instances serve requests. Existing deployment-wide operation locking requirements still apply.

Return URLs are restricted to local Upgrade Center routes. Sign out ends only the local Upgrade Center session; it does not sign out of the product. The existing GET logout link opens a local confirmation form in Standalone Mode. IDP-integrated logout keeps its existing behavior.

## Existing Workflows

Standalone Mode changes the configuration/key providers and browser authentication. It retains the existing:

- Product selection, release/custom-patch discovery, and current-version APIs.
- Master and tenant connection decryption and TenantTypeId filtering (BI 3, Reports 4).
- Database schema/prefix handling, affected-table backup/restore, and eligibility restrictions.
- Kubernetes rollout, version-specific Playwright images, runtime Secrets, result/report uploads, shared state, and cleanup.
- Upgrade/rollback history, cancellation, and restart recovery through existing master database storage.

The mode is independent of IDP service APIs for configuration and local authentication, not independent of the running product and its database. Product version/health endpoints and Playwright still require application access. Kubernetes remains the default execution environment. The separately selected [ECS backend](ecs-deployment.md) supports BI Standalone on Linux Fargate; selecting Standalone alone does not enable ECS, Docker, Linux-host, or Windows execution.

Keep Upgrade Center's writable data-protection keys and Playwright reports/state separate from product configuration. No product storage backup or additional product-table migration is introduced by this mode.

## Deployment Requirements

BI, Common, and Reports Helm charts skip the IDP configuration init container in Standalone Mode. The BI/Reports kubectl manifests skip the IDP API wait in this mode. Existing installations continue using IdpIntegrated by default.

Configure existing product storage and the administrator Secret. Read-only configuration access and writable Upgrade Center storage remain separate. Helm validates the selected backend and administrator settings. Restart Upgrade Center after changing existing Secrets or mounted configuration. Product connectivity and storage permissions must be validated in the deployed environment. Without a product PV, existing storage settings are also passed in IDP-integrated mode to persist Upgrade Center's reports/keys. The Common product chart's own PVC requirement is unchanged; there is no Common kubectl deployment option.

## Verification

```powershell
dotnet build Bold.UpgradeCenter.csproj
dotnet test tests/Bold.UpgradeCenter.Tests/Bold.UpgradeCenter.Tests.csproj
```

Regression tests use generated encrypted sample files, mocked storage SDK clients, simulated database readers, and an in-process web host. Live bucket/container permissions, identity authentication, database runtime lookup, Kubernetes upgrade/rollback, and mounted-volume permissions must still be verified during deployment integration testing.
