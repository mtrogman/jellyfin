# Jellyfin Performance Fork — mtrogman/jellyfin

## Status: Maintenance Mode
This repo is in **maintenance mode**. No active feature development — we simply rebase onto
each new upstream Jellyfin release, apply the existing performance patches, and publish a
Docker image.

## Purpose
This is the **performance-focused fork** of Jellyfin, published as Docker images under `mtrogman/jellyfin`.
It tracks upstream `jellyfin/jellyfin` and layers performance optimizations on top.

## Repo & Remote Layout
- **origin**: `https://github.com/mtrogman/jellyfin.git` (our fork)
- **upstream**: `https://github.com/jellyfin/jellyfin.git` (official Jellyfin)

## Branch Strategy

**`master` is a clean mirror of upstream Jellyfin.** Never commit fork-specific files
(Dockerfile, build workflows, etc.) to master. It must stay identical to `upstream/master`
so it can be synced freely and used as a base for upstream PRs without contamination.

All fork-specific work lives on **release branches**.

| Branch | Purpose |
|--------|---------|
| `master` | **Clean upstream mirror.** Never put fork-specific files here. |
| `<version>` (e.g. `10.11.7`) | Release branch. Contains performance commits + Dockerfile + build workflow. |
| `feature/*` | Feature branches for upstream PRs (e.g. `feature/directoryservice-lru-cache`) |

## Image Naming Convention
The image tag **always matches the upstream Jellyfin version exactly**. One version, one tag — no revision suffixes.

| Upstream release | Our image tag |
|------------------|---------------|
| `jellyfin/jellyfin:10.11.7` | `mtrogman/jellyfin:10.11.7` |
| `jellyfin/jellyfin:10.12.0` | `mtrogman/jellyfin:10.12.0` |

The workflow also tags the image as `mtrogman/jellyfin:latest`.

## CI/CD — GitHub Actions

### Build files live on release branches only
Since master is a clean upstream mirror, the Dockerfile and build workflow **only exist on
release branches**. They are cherry-picked/recreated from the previous release branch when cutting a new one.

- **`Dockerfile`** — Multi-stage build. Compiles self-contained, then overlays binaries onto `jellyfin/jellyfin:<version>` (keeps official ffmpeg, web UI, drivers, fonts).
- **`.github/workflows/build-pragma-image.yml`** — Builds and pushes `mtrogman/jellyfin` to Docker Hub.
- **Secrets required**: `DOCKER_USERNAME` and `DOCKER_TOKEN` in GitHub repo settings.

### What to update for each new release
The workflow has the version and branch name **hardcoded**. When cutting a new release branch,
update these 4 values:

1. **Trigger branch** in `build-pragma-image.yml` — change to match the new branch name:
   ```yaml
   on:
     push:
       branches:
         - <new-version>   # e.g. 10.11.7
   ```

2. **`IMAGE_TAG`** — set to the version:
   ```yaml
   echo "IMAGE_TAG=<new-version>" >> $GITHUB_ENV
   ```

3. **`JELLYFIN_BASE_TAG`** build arg — must match the upstream release used as the runtime base:
   ```yaml
   build-args: |
     JELLYFIN_BASE_TAG=<new-version>
   ```

4. **Dockerfile `ARG`** default — update the default `JELLYFIN_BASE_TAG` to match:
   ```dockerfile
   ARG JELLYFIN_BASE_TAG=<new-version>
   ```

## Workflow: Cutting a New Release
1. `git fetch upstream`
2. `git checkout master && git merge upstream/master` (keep master clean)
3. `git checkout -b <new-version> v<new-version>` (create branch from the upstream release tag)
4. Cherry-pick performance commits from previous release branch (see commit history on prior release branch)
5. Add/update Dockerfile and workflow with the new version values
6. Push the branch — CI builds and pushes `mtrogman/jellyfin:<new-version>`

### Performance commits to cherry-pick
These are the core performance patches (applied in order). Cherry-pick from the previous
release branch — the SHAs will differ per branch but the commit messages are stable:

1. Performance optimizations for large databases (Phase 1)
2. Performance optimizations Phase 2: Query patterns and configuration
3. Performance optimizations Phase 3: Memory stability and query fixes
4. Performance optimizations Phase 4: Query efficiency and configuration
5. Fix duplicate filtered Include error on UserData navigation
6. Fix DbUpdateConcurrencyException during concurrent user logins
7. Address PR feedback: revert disputed optimizations
8. Address PR comments (composite indexes, revert PragmaConnectionInterceptor)

Note: The workflow file will conflict on each cherry-pick (since it's removed and re-added).
Just `git rm` the conflicted file and `git cherry-pick --continue` each time, then add the
fresh Dockerfile + workflow as a final commit.

## Build (local)
```bash
dotnet publish Jellyfin.Server --configuration Release --self-contained -r linux-x64
```

## What Does NOT Belong Here
- PostgreSQL/Redis/HA infrastructure → see `../jellyfin-ha`
- Docker compose files for HA deployments → see `../jellyfin-ha`
- Migration scripts and runbooks → see `../jellyfin-ha`
