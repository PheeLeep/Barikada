# Sipat

*Sipat* (Filipino: to sight, to inspect closely) is Barikada's scout. Barikada
is the wall — three blocklists of Philippine gambling ("scatter") sites. Sipat
walks the perimeter: it finds new domains, verifies them against independent
sources, and feeds confirmed ones back into the wall.

```
sipat pagcor                       # licensed operators Barikada doesn't block yet
sipat scan -d phtaya23.com         # is this sinkholed / live / dead? listed already?
sipat discover -k phtaya,jili      # sibling domains from certificate transparency
sipat audit --dns                  # health-check the three lists (report only)
sipat emit -d bad.com,worse.ph     # append confirmed domains to all three lists
sipat cache                        # what's cached, how old
```

## How it sees

Sipat classifies domains by comparing **two DNS vantage points**:

- **Your ISP's resolver** — inside the Philippines, domains the government has
  ordered blocked resolve to the CICC sinkhole (`blocked.sbmd.cicc.gov.ph`'s
  block page). A sinkhole answer means the state has already classified that
  domain as illegal gambling: independent, authoritative confirmation from a
  single DNS query.
- **A public DoH resolver** (Cloudflare) — outside the block, returning the
  operator's real infrastructure, which tells you whether the site is actually
  alive or just parked.

Because the whole signal depends on the first vantage point, `scan` runs a
**canary preflight**: it resolves a few known-sinkholed domains first, and if
none hit the sinkhole (VPN active, foreign DNS configured) it refuses to run
rather than silently classifying everything as clean. `--skip-canary` degrades
to liveness-only checks. The sinkhole IP set is the known static address plus
whatever the block page itself resolves to at runtime, as a backup for when
CICC moves infrastructure.

## Sources of truth

| Source | Nature | Used for |
|---|---|---|
| CICC sinkhole (ISP DNS) | live, authoritative | confirming a domain is state-blocked |
| CICC block page roster | live | the PAGCOR-approved operator list (~53 landing domains) |
| PAGCOR provider PDF | dated snapshot | breadth: every registered URL per licensee |
| crt.sh (certificate transparency) | live, laggy | discovering sibling/numbered domains that actually exist |
| `git main` blob / raw.githubusercontent | canonical | what Barikada already blocks |

The PAGCOR PDF is replaced in place without stable dating — the file online can
be *older* than one you saved earlier. Sipat parses the "as of" date out of the
document, always displays it, warns when it's stale, and measures freshness
against the live CICC roster instead of trusting the snapshot.

## Caching

Every remote source is cached under `~/.cache/sipat` (respects
`XDG_CACHE_HOME`), and every result says where it came from — e.g.
`blocklist via git main @ 9c65665` or `remote (etag-validated cache)`:

- **Blocklists** — read from the committed state of `main` in a checkout
  (stamped with the commit hash); elsewhere fetched with ETag conditional GETs
  (GitHub answers 304 when unchanged).
- **crt.sh results** — 24h TTL; a degraded crt.sh falls back to the stale
  cache with an explicit warning. `--refresh` forces a live query.
- **PAGCOR PDF** — 7-day TTL, same stale-fallback behavior, `--refresh` to force.
- **DNS is never cached.** Sinkhole verdicts are point-in-time truth.

When a source fails and a stale cache is used, the provenance line says so.
`sipat cache` shows everything; `sipat cache --clear` wipes it.

## Guardrails

- **`.gov.ph` and `pagcor.ph` can never be emitted.** A failed redirect landing
  on the block page, or the regulator's own domain appearing in its PDF, is
  refused by policy before any list is touched.
- **`audit` is report-only.** It never deletes: dead gambling domains get
  re-registered, so a stale entry costs nothing and removing it can cost a lot.
- **A live-but-not-sinkholed domain is never auto-added.** CICC confirmation or
  your own review — `discover` output is candidates, not verdicts.
- **`emit` is all-or-nothing** and dedupes against both the committed `main`
  state and the working tree. It writes the same section-anchored format the
  lists already use (default: POGO/illegal section; `--pagcor` for the
  licensed section), bumping the section's date line.

## Requirements

- .NET 10 SDK
- [PheeLeep.ArgSharp](https://www.nuget.org/packages/PheeLeep.ArgSharp) and
  [Spectre.Console](https://www.nuget.org/packages/Spectre.Console) (restored
  from NuGet on build)
- `pdftotext` (poppler-utils) — only for the PAGCOR PDF source; everything
  else works without it
- A Philippine ISP resolver for sinkhole verdicts (see canary preflight above)

```sh
cd sipat
dotnet build
dotnet run -- pagcor
```

## Commands

Domains are always given comma-separated (`-d a.com,b.com`), via a file
(`-f list.txt`, one per line, `#` comments), or both. URLs are fine — they are
normalized down to bare domains.

| Command | What it does |
|---|---|
| `pagcor` | Fetches CICC's approved-operator roster and the PAGCOR PDF; reports licensed domains Barikada doesn't cover. `--missing-only`, `--refresh` |
| `scan` | Two-vantage classification: `SINKHOLED` / `live` / `dead`, plus whether each domain is already listed. `--sinkhole` adds IPs, `--skip-canary` for VPN runs |
| `discover` | crt.sh substring search by brand keyword (`phtaya.com` reduces to `phtaya`; keywords under 4 chars are refused as too noisy). `--output` writes candidates for `scan -f` |
| `audit` | Duplicates, malformed entries, cross-file drift (wildcard-coverage aware), protected domains; `--dns` adds CICC-overlap and dead-entry stats |
| `emit` | Appends confirmed domains to all three lists in place. `--pagcor`, `--dry-run` |
| `cache` | Show cached files with age; `--clear` |

Exit codes: `0` clean, `1` error, `2` findings/inconclusive (audit findings, or
discovery where every source failed).
