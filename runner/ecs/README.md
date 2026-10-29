# ECS Runner State Handoff

ECS uses the same version-specific `playwrightRunnerImage` from `/version/json/{version}/k8s`. There is no separate ECS runner image, Dockerfile, or release catalog.

With EFS, Upgrade Center sets the selected image and `pre`, `post`, or `cleanup` command, leaving the image entrypoint unchanged. The existing shared-directory handling is reused.

For S3-only deployments, these JavaScript modules are embedded in the Upgrade Center assembly. The ECS adapter supplies them in memory through the runner container's `node -e` startup command. The bootstrap invokes `/app/docker-entrypoint.js` from the selected image; it does not replace its tests, configuration generation, result/report uploads, or image user. No files are installed in the runner image and no automation-repository change is required. Credentials and signed URLs remain in runtime Secret references, not the startup command.

The bootstrap restores ResponseLogs/storage before post/cleanup and saves them after execution, including failed tests. A failed restore never runs tests or overwrites saved state. State is one integrity-checked job object, bounded to a 64 MiB envelope and 5000 files; unsafe paths and symbolic links are rejected. Product configuration is never included. Bootstrap failures return exit code 1, even if the inner runner uploaded a passing summary.

The image must provide Node.js 18 or later with global `fetch`/`AbortSignal.timeout`, `/app/docker-entrypoint.js`, the existing environment/upload protocol, and pre/post/cleanup support. Verify the actual version-specific images on Fargate before rollout. A different image layout requires an explicit adapter change; do not silently use another version's image.

Leave `entryPoint` unset in the runner template. Upgrade Center overrides it only for S3-only state transfer. Set the runner container `stopTimeout` to 120 seconds so partial state has time to upload during graceful cancellation. Kubernetes task execution is unchanged.
