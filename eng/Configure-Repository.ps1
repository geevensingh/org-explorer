$ErrorActionPreference = 'Stop'
$originalGhToken = $env:GH_TOKEN
$originalGithubToken = $env:GITHUB_TOKEN
try {
    $env:GH_TOKEN = ''
    $env:GITHUB_TOKEN = ''
    $env:GH_TOKEN = gh auth token --user geevensingh
    if ($LASTEXITCODE -ne 0 -or (gh api user --jq .login) -ne 'geevensingh') { throw 'Expected geevensingh account.' }
    if ((gh repo view geevensingh/org-explorer --json nameWithOwner --jq .nameWithOwner) -ne 'geevensingh/org-explorer') {
        throw 'Repository routing failed.'
    }
    $repo = 'repos/geevensingh/org-explorer'
    gh api --method PATCH $repo -F allow_squash_merge=true -F allow_merge_commit=false -F allow_rebase_merge=false --silent
    if ($LASTEXITCODE -ne 0) { throw 'Cannot configure squash-only repository merges.' }
    $policy = @{
        name = 'Main requires passing CI PRs'; target = 'branch'; enforcement = 'active'; bypass_actors = @()
        conditions = @{ ref_name = @{ include = @('refs/heads/main'); exclude = @() } }
        rules = @(
            @{ type = 'pull_request'; parameters = @{
                required_approving_review_count = 0; dismiss_stale_reviews_on_push = $true
                require_code_owner_review = $false; require_last_push_approval = $false
                required_review_thread_resolution = $true; allowed_merge_methods = @('squash')
            } }
            @{ type = 'required_status_checks'; parameters = @{
                required_status_checks = @(@{ context = 'CI'; integration_id = 15368 })
                strict_required_status_checks_policy = $true; do_not_enforce_on_create = $false
            } }
            @{ type = 'non_fast_forward' }
            @{ type = 'deletion' }
        )
    }
    $existing = @(gh api "$repo/rulesets" | ConvertFrom-Json | Where-Object name -eq $policy.name)
    if ($LASTEXITCODE -ne 0 -or $existing.Count -gt 1) { throw 'Cannot resolve a unique repository ruleset.' }
    $method = if ($existing.Count -eq 1) { 'PUT' } else { 'POST' }
    $endpoint = if ($existing.Count -eq 1) { "$repo/rulesets/$($existing[0].id)" } else { "$repo/rulesets" }
    $result = $policy | ConvertTo-Json -Depth 12 -Compress | gh api --method $method $endpoint --input - | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Ruleset configuration failed.' }
    $live = gh api "$repo/rulesets/$($result.id)" | ConvertFrom-Json
    $settings = gh api $repo | ConvertFrom-Json
    $checks = $live.rules | Where-Object type -eq 'required_status_checks'
    $pr = $live.rules | Where-Object type -eq 'pull_request'
    if ($live.enforcement -ne 'active' -or $live.bypass_actors.Count -ne 0 -or
        $live.conditions.ref_name.include -notcontains 'refs/heads/main' -or
        !$pr -or $pr.parameters.allowed_merge_methods -notcontains 'squash' -or
        !$checks.parameters.strict_required_status_checks_policy -or
        $checks.parameters.required_status_checks[0].context -ne 'CI' -or
        $checks.parameters.required_status_checks[0].integration_id -ne 15368 -or
        !$settings.allow_squash_merge -or $settings.allow_merge_commit -or $settings.allow_rebase_merge) {
        throw 'Live GitHub policy does not match required owner-enforced CI PRs and squash-only merges.'
    }
    Write-Host "Verified active main ruleset $($live.id), no bypass actors, required GitHub Actions CI, squash-only merges."
}
finally {
    $env:GH_TOKEN = $originalGhToken
    $env:GITHUB_TOKEN = $originalGithubToken
}
