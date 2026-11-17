---
name: code-commit
description: Must be invoked when the user asks to commit code, submit code, or any similar request.
---

# Code Commit Skill

## Commit Workflow

### 1. Review Changes

Run `git status` and `git diff` in the repository to understand all current changes.

### 2. Pre-commit Checks

Start with the automated gate (2.1), then review all changed files by hand (2.2-2.4).
**If any issues are found, report them to the user and wait for confirmation before committing.**

#### 2.1 Automated Gate (blocking)

```bash
dotnet build UnpackStation.sln
dotnet test UnpackStation.sln --no-build
```

Exit 0 to proceed. **A non-zero exit blocks the commit** — report the output and
stop; do not commit and fix afterwards.

Do not bypass the gate with `--no-verify` or by skipping checks. If a check
genuinely should not apply, say so to the user and let them decide.

#### 2.2 Temporary Code Detection

Use your judgment to identify code that looks temporary or was clearly left in by accident -- things like `debugger`-equivalent breakpoints, placeholder values (`test123`, `asdf`, `foo`), commented-out code blocks, or anything that reads like a quick hack not meant for production. Don't be overly rigid; focus on what obviously doesn't belong.

#### 2.3 Temporary File Detection

Check whether any files that should not be committed are included in the changes:

- OS files: `.DS_Store`, `Thumbs.db`, etc.
- Temp files: `*.log`, `*.tmp`, `*.bak`, `*.swp`, etc.
- IDE configs: non-shared files under `.idea/`, `.vs/`, etc.
- Build artifacts: `bin/`, `obj/`, `TestResults/`, `publish/`, etc.
- Sensitive files: `.env`, credentials, private keys, etc.

#### 2.4 Open-source Readiness

Ensure the code is suitable for an internationalized, open-source project:

- **Comments and documentation in English** to align with open-source conventions.
- **No pinyin naming** for variables, functions, or identifiers. Use meaningful English names.
- **No internal/private information** such as internal IP addresses, intranet domains, personal emails, phone numbers, API keys, or secrets.
- **No specific company or brand names** in code, comments, or commit messages — this includes but is not limited to Microsoft, Google, Tencent, Alibaba, Apple, Meta, Amazon, Baidu, ByteDance, etc. Use generic terms instead (e.g., "cloud provider", "search engine", "platform").

### 3. Generate Commit Message

Compose the message according to the **AngularJS commit message convention** below.
The goal is a readable history that can be analyzed for changelog generation.

#### 3.1 Overall Structure

Each message consists of a `header`, a `body`, and a `footer`, separated by blank lines:

```
<header>
<BLANK LINE>
<body>
<BLANK LINE>
<footer>
```

- The `header` is mandatory.
- The `body` is mandatory for all commits except those of type `docs`. When present it must be at least 20 characters long.
- The `footer` is optional.

#### 3.2 Header

```
<type>(<scope>): <short summary>
  │       │             │
  │       │             └─⫸ Summary in present tense. Not capitalized. No period at the end.
  │       │
  │       └─⫸ Commit Scope: optional. The area affected by the change.
  │
  └─⫸ Commit Type: build|ci|docs|feat|fix|perf|refactor|test
```

The `<type>` and `<summary>` fields are mandatory; `(<scope>)` is optional.

**Type** — must be one of:

| Type         | Description                                                                                                   |
| ------------ | ------------------------------------------------------------------------------------------------------------- |
| **build**    | Changes that affect the build system or external dependencies (e.g. project files, package versions, 7z.dll)   |
| **ci**       | Changes to CI configuration files and scripts (e.g. GitHub Actions)                                            |
| **docs**     | Documentation only changes                                                                                     |
| **feat**     | A new feature                                                                                                  |
| **fix**      | A bug fix                                                                                                      |
| **perf**     | A code change that improves performance                                                                        |
| **refactor** | A code change that neither fixes a bug nor adds a feature                                                      |
| **test**     | Adding missing tests or correcting existing tests                                                              |

**Scope** — optional; name the area affected as seen by a reader of the generated changelog.
Useful scopes for this project include `engines`, `models`, `services`, `viewmodels`,
`ui`, `packaging`, `changelog`, `docs-infra`, and `migrations`. Omit the scope for
cross-cutting changes (e.g. `test: add missing unit tests`) and for docs changes not
tied to a specific area (e.g. `docs: fix typo in README`).

**Summary** — a succinct description of the change:

- use the imperative, present tense: "change" not "changed" nor "changes"
- don't capitalize the first letter
- no dot (.) at the end

#### 3.3 Body

Just as in the summary, use the imperative, present tense: "fix" not "fixed" nor "fixes".

Explain the **motivation** for the change — _why_ it was made. Include a comparison
of the previous behavior with the new behavior when it helps illustrate the impact.
Do not merely repeat the summary.

#### 3.4 Footer

The footer contains information about breaking changes, deprecations, and references
to GitHub issues or PRs that this commit closes or relates to.

Breaking change:

```
BREAKING CHANGE: <breaking change summary>
<BLANK LINE>
<breaking change description + migration instructions>
<BLANK LINE>
<BLANK LINE>
Fixes #<issue number>
```

Deprecation:

```
DEPRECATED: <what is deprecated>
<BLANK LINE>
<deprecation description + recommended update path>
<BLANK LINE>
<BLANK LINE>
Closes #<pr number>
```

A Breaking Change section starts with `BREAKING CHANGE: ` followed by a brief summary,
a blank line, then a detailed description including migration instructions. A
Deprecation section follows the same pattern starting with `DEPRECATED: `.

#### 3.5 Revert Commits

If a commit reverts a previous commit, begin with `revert: ` followed by the header of
the reverted commit. The body must contain:

- the SHA of the reverted commit in the format `This reverts commit <SHA>`,
- a clear description of the reason for reverting.

#### 3.6 Examples

Message with a body and a footer reference:

```
git commit \
  -m "feat(services): add retry to archive extraction" \
  -m "Retry failed extraction up to three times with a short delay so transient file locks do not abort the whole batch." \
  -m "Fixes #42"
```

Concise message without a body (allowed for `docs` only):

```
git commit -m "docs: fix typo in README"
```

Breaking change:

```
git commit \
  -m "feat(packaging): change default output directory" \
  -m "Move the default release output from the app folder to a dedicated releases folder. Update any saved paths before upgrading." \
  -m "BREAKING CHANGE: default release output path has changed."
```

Use repeated `-m` flags so git inserts the required blank lines between
header, body, and footer. Do not use `-m` with literal `\n` sequences.

### 4. Execute Commit

```bash
git add <relevant files>
git commit -m "<header>" -m "<body>" -m "<footer>"
```

- Only stage files that pass the checks. Do not blindly `git add .`.
- Run `git status` after committing to verify.

## Notes

- If there are no changes, inform the user that there is nothing to commit.
- Do not run `git push` unless the user explicitly asks.
- When issues are found during checks, list all of them and ask the user how to proceed. Do not skip issues on your own.
