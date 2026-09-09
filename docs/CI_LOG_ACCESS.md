# GitHub Actions log access policy

This document defines the durable project policy for obtaining GitHub Actions diagnostics during Clipensk development.

## Goal

Development must not stall merely because one particular GitHub Actions log retrieval path is unavailable, incomplete, oversized, delayed, or otherwise unsuitable.

The required outcome is enough primary diagnostic evidence to identify the actual failure (for example failing test, assertion, exception, stack trace, compiler error, command output, or native build error) and continue with the smallest justified fix.

## Retrieval escalation

Use the least invasive reliable path first, but do not treat any previous log-access instruction as an artificial hard boundary when it prevents a complete result.

1. Try the connected GitHub job/run log facilities when they provide usable output.
2. Prefer deterministic workflow artifacts when available. The Build workflow persists `artifacts/test-output.txt` as a GitHub Actions artifact while preserving the original `dotnet test` exit code.
3. If direct logs are unavailable or incomplete, use other available and approved GitHub access paths or project-safe diagnostic techniques as necessary to obtain the missing evidence. This may include GitHub REST resources exposed by the connected tooling, run/job metadata, artifacts, or a narrowly scoped diagnostic workflow change on a non-production branch.
4. Do not change product behavior merely to make CI output easier to read. Diagnostic changes must remain separable from product changes and must preserve the command's real exit status.
5. Before any durable repository mutation, perform fresh GitHub TOCTOU checks required by the current handoff/promotion discipline.

The assistant is explicitly authorized to go beyond a previously described GitHub Actions log-access procedure when that is necessary for completeness and for delivering the requested development result, subject to the project's normal safety, scope, and promotion rules.

## Manual fallback: ask the user for the log

If automated access still cannot provide the required diagnostic evidence, the assistant must not guess from a red CI status. It must explicitly tell the user which exact workflow run/job or artifact is needed and ask the user to upload it to the chat.

Give concrete instructions. Preferred order:

### A. Download an existing diagnostic artifact

1. Open the Clipensk repository on GitHub.
2. Open **Actions** and select the requested workflow run.
3. On the run summary page, scroll to **Artifacts**.
4. Download the requested artifact, normally `test-output-<commit-sha>` when present.
5. Upload the downloaded ZIP to the chat without modifying its contents.

### B. Download the GitHub Actions log archive

If no useful artifact exists:

1. Open the Clipensk repository on GitHub.
2. Open **Actions** and select the exact workflow run requested by the assistant.
3. Use the run page menu (the `...` menu; wording can vary slightly in GitHub UI) and choose **Download log archive**.
4. If the assistant asked for a specific job and GitHub offers a job-specific download, that is also acceptable.
5. Upload the downloaded ZIP to the chat without modifying its contents.

When asking for a manual upload, the assistant must identify at least the workflow/run and, when known, the job ID or artifact name so the user does not have to guess which log is required.

## Evidence and reporting

A CI failure is not considered diagnosed solely because a workflow or step has conclusion `failure`.

For test failures, report the concrete failing test(s), assertion/exception details, and relevant stack trace whenever the log provides them. For build/native failures, report the failing command/tool and the decisive error lines.

If a user-provided ZIP is used, state that explicitly in the development report. If the assistant retrieved the evidence independently through GitHub artifacts or another connected path, state that explicitly as well.

## Proven self-service path

The artifact approach was validated end-to-end during the Archive external-reference cleanup phase:

- failing Build run `34380011012` published artifact `10115561837`;
- the artifact was downloaded through the connected GitHub tooling and contained `test-output.txt`;
- that output exposed the exact two failing Archive tests and their `InvalidDataException` stack traces;
- the resulting minimal fixture correction was committed as `01677d7bfb577848118df6c9c5489f9e4e36441e`;
- validation run `34380980853` then completed successfully.

This proves the intended fallback chain: workflow run -> artifact metadata -> artifact ZIP -> `test-output.txt` -> exact diagnosis, without requiring a manual user upload when the artifact route works.
