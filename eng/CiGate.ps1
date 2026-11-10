function Assert-CiResults {
    param([AllowEmptyCollection()][AllowEmptyString()][string[]] $Results)
    if ($Results.Count -ne 3 -or @($Results | Where-Object { $_ -cne 'success' }).Count -ne 0) {
        throw "Required CI lanes did not all succeed: $($Results -join ', ')"
    }
}
