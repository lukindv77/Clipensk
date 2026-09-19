# GitHub Actions log access runbook

This document defines the supported way to obtain GitHub Actions diagnostics for Clipensk without asking a maintainer to copy logs manually.

## Required access

The GitHub integration used to inspect the repository must be installed for `lukindv77/Clipensk` and have repository permission `Actions: read`.

`Contents: read` is also expected so the workflow definition and the exact commit under test can be inspected.

`Actions: write` is only needed for optional operations such as rerunning a failed job. It is not required to read runs, jobs, logs, or artifacts.

Do not paste personal access tokens, GitHub App private keys, OAuth tokens, or other secrets into chat. If access stops working, reauthorize the existing GitHub integration and verify that this repository is included in its installation scope.

## Preferred retrieval sequence

Always anchor diagnostics to an exact commit SHA. Do not infer a failure from a run belonging to a different SHA.

1. Find the workflow run for the exact SHA and record its `run_id`, workflow name, status, and conclusion.
2. Read the jobs for that run and record the failed `job_id`.
3. Read the job step summaries to identify the failing step.
4. After the job is completed, fetch the decoded job log for that `job_id`.
5. Extract the exact failing test or command, assertion/error message, and stack trace before changing code.

Relevant GitHub REST endpoints are:

```text
GET /repos/{owner}/{repo}/actions/runs/{run_id}/jobs
GET /repos/{owner}/{repo}/actions/jobs/{job_id}/logs
```

The job-log endpoint is served through a temporary redirect. A just-finished or still-running job can temporarily return a storage error such as `BlobNotFound`; treat that as a transient log-finalization condition rather than proof that repository access is missing.

## Artifact fallback

Raw job logs are not sufficiently reliable as the only diagnostic channel. Test workflows should additionally persist plain-text test output as a GitHub Actions artifact.

Recommended pattern for the Build workflow:

```yaml
- name: Test
  shell: pwsh
  run: |
    New-Item -ItemType Directory -Force -Path artifacts | Out-Null
    & dotnet test Clipensk.slnx --configuration Release --no-build 2>&1 |
      Tee-Object -FilePath artifacts/test-output.txt
    $testExitCode = $LASTEXITCODE
    if ($testExitCode -ne 0) {
      exit $testExitCode
    }

- name: Upload test diagnostics
  if: always()
  uses: actions/upload-artifact@v4
  with:
    name: test-output-${{ github.sha }}
    path: artifacts/test-output.txt
    if-no-files-found: warn
    retention-days: 7
```

This keeps the actual `dotnet test` invocation unchanged, mirrors stdout/stderr to the normal Actions console, preserves the original exit code, and uploads the same diagnostic text even when tests fail.

Retrieve artifacts with:

```text
GET /repos/{owner}/{repo}/actions/runs/{run_id}/artifacts
GET /repos/{owner}/{repo}/actions/artifacts/{artifact_id}/{archive_format}
```

For Clipensk, the expected diagnostic artifact naming convention is:

```text
test-output-{exact-github-sha}
```

After downloading the ZIP, inspect `test-output.txt` and record the exact test name, error/assertion, and stack trace.

## Diagnostic decision rule

Use this order:

1. exact-SHA run metadata;
2. job and step summaries;
3. raw completed job log;
4. `test-output-{sha}` artifact when the raw log is unavailable or incomplete.

A code change must not be based only on a red workflow badge. The failure must first be reduced to concrete evidence from the exact SHA.

## Security and retention

Diagnostic artifacts must contain test/build output only. Do not intentionally capture environment-variable dumps, authentication material, signing credentials, repository secrets, user data, or database contents.

Keep diagnostic retention short unless longer retention is required for an investigation. Seven days is the default for the Clipensk test-output artifact.

## Known working proof

The fallback path was validated end-to-end during Archive external-reference cleanup:

- failed run `34380011012` published diagnostic artifact `10115561837`;
- the artifact exposed the exact failing tests and `InvalidDataException` stack trace;
- the resulting test-fixture correction was validated by successful run `34380980853`.

That incident is the reference proof that Actions artifacts can be retrieved and parsed without requiring a maintainer to manually download and upload logs.