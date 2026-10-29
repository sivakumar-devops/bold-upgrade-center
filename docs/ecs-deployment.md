# ECS Execution Backend

ECS is opt-in through `UpgradeCenter__ExecutionEnvironment=ECS`. The default remains Kubernetes. ECS currently requires Standalone integration and BoldBI deployment mode; Reports and Common are deliberately rejected. Product selection, database planning, authentication, history, and release-family runner selection retain the existing contracts.

See the deployment examples and complete installation/validation guide in `boldbi-server-in-docker/deploy/ECS-deployment-manifest/Multi container/UpgradeCenter/README.md`. The examples cover EFS, S3-only, and EFS+S3. S3-only requires no EFS. The existing Kubernetes storage templates are unchanged.

## Implementation Boundaries

- `EcsUpgradeService` implements the existing rollout/recovery interface. `KubernetesUpgradeResult.Ecs` is an additive persisted task-revision plan; old Kubernetes payloads remain readable. The legacy stage/operation IDs are retained for history compatibility, with ECS labels in monitoring.
- `EcsPlaywrightJobService` schedules real one-off Fargate tasks, rather than the health-only fallback used when Kubernetes jobs are disabled. ECS forces external validation on, even if the legacy `UseKubernetesJob` setting is false.
- Runtime database/credential/SSL resolution is reused from the tested Playwright configuration provider, not independently reimplemented.
- `EcsValidationExecutions` persists pinned runner definitions, task ARNs, Secret ARNs, idempotency tokens, and verified terminal summaries. Secrets and raw credentials are not stored in those recovery fields.
- ECS reuses `/version/json/{version}/k8s` for product images and `playwrightRunnerImage`. Component names map to configured ECS services/containers. No ECS-specific release records, endpoint, or runner image are needed. Missing compatible mappings stop the upgrade before backup.
- EFS uses isolated job directories and the selected image's existing entrypoint. For S3-only handoff, Upgrade Center embeds the transport in `runner/ecs` and supplies it as a non-secret `node -e` startup command. It calls `/app/docker-entrypoint.js` inside the same version-specific image. Existing test scripts and Kubernetes/EFS handling are not replaced.
- No EFS/PVC, buckets, product services, ALB rules, or IAM roles are provisioned automatically. The ECS JSON examples are inputs for an operator, not an automatic account installer.
- The initial implementation supports ECS rolling-controller Linux Fargate services only. Operator-managed maintenance is required. Existing database support/eligibility limitations still apply.

## Verify

```powershell
dotnet test tests/Bold.UpgradeCenter.Tests/Bold.UpgradeCenter.Tests.csproj --configuration Release
```

Run `node --test tests/ecs-runner/shared-state.test.cjs` from this repository. Publish the updated Upgrade Center image and reuse the existing release API and Playwright images. No automation-repository edit or separate runner build is required. Verify the selected images support the documented entrypoint contract and S3 bootstrap requirements in `runner/ecs/README.md`. A live AWS upgrade/rollback test remains mandatory.
