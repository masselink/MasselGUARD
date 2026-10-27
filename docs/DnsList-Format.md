# DNS server list: repository layout and file format

The list behind **Browse DNS servers** lives in its own repository, `masselink/MasselGUARD-dnslist`. That repository holds **only the list**: an index and one file per provider. No scripts, workflows or documentation are kept there; this page is the reference.

```
index.json                 which provider files exist (and a version label)
servers/adguard.json       one provider with all of its resolvers
servers/cloudflare.json
servers/mullvad.json
...
```

**The list is language-neutral.** Names are the services' own names, and the only free text is one short `description` in the list's own language (English). Everything else is a code that MasselGUARD translates from its own language files (what a server blocks, how it logs, which value you must fill in), so there is one list for all languages and nothing to translate when a server is added.

The app downloads `https://raw.githubusercontent.com/<owner>/<repo>/main/index.json` and then every `servers/<name>.json` the index names. It re-reads them at most once a day, sends each file's ETag so unchanged files are not downloaded again, and stores a complete copy in `%APPDATA%\MasselGUARD\dnslist`. The refresh is **all or nothing**: if one file is missing or not valid, the previous copy stays, so users never see a mix of two versions. The app ships no copy of the list: it is fetched from the repository when needed. The servers a user adds are also saved as a local copy (`%APPDATA%\MasselGUARD\dns-selected.json`), so they stay visible, and can be unticked again, when the list cannot be downloaded or a server leaves the list.

## Adding or changing a provider

1. Add or edit `servers/<name>.json` (`<name>`: lower-case letters, digits and hyphens, at most 41 characters).
2. For a new provider, add `<name>` to `providers` in `index.json` **and** change `version` (any text, for example the date). A file that is not in the index is ignored.
3. Check that the app accepts it: the app silently **skips** an entry that breaks the rules below. Run `MasselGUARDcli dns check-list <folder of the dnslist checkout>`: it reads `index.json` and every provider file with the same parser as the app and reports, per file, how many servers are valid and how many would be skipped (invalid, duplicate id, private address, missing file, a file the index does not list). Exit code 1 when anything is wrong. Browse DNS servers also shows "N invalid entries ignored".

## `index.json`

```json
{ "schemaVersion": 1, "version": "2026-10-06", "providers": ["adguard", "cloudflare", "mullvad"] }
```

At most 100 names, the file at most 16 KB. Names that are not valid or are repeated are ignored.

## Provider file

```json
{
  "schemaVersion": 1,
  "provider": "Quad9",
  "website": "https://quad9.net/",
  "privacyPolicy": "https://quad9.net/privacy/",
  "country": "CH",
  "servers": [
    {
      "id": "quad9",
      "name": "Quad9 Secured",
      "description": "Non-profit resolver that blocks domains known to be malicious.",
      "blocks": ["malware"],
      "logging": "minimal",
      "v4": ["9.9.9.9", "149.112.112.112"],
      "v6": ["2620:fe::fe", "2620:fe::9"],
      "doh": "https://dns.quad9.net/dns-query"
    },
    {
      "id": "nextdns",
      "name": "NextDNS",
      "description": "A resolver you configure yourself on nextdns.io.",
      "blocks": [],
      "logging": "configurable",
      "doh": "https://dns.nextdns.io/CONFIG_ID",
      "parameters": ["CONFIG_ID"]
    }
  ]
}
```

The file is at most 64 KB and holds at most 50 servers. `provider`, `website`, `privacyPolicy` and `country` are written once and apply to every server of the file.

| Field | Required | Meaning |
|---|---|---|
| `schemaVersion` | yes | `1`. Any other value makes the app ignore the file. |
| `provider` | no | Company or project name (max 80). |
| `website`, `privacyPolicy` | no | `https://` links, max 200. Shown as links in the picker. |
| `country` | no | Two-letter country code of the operator. The app shows the country name in the user's language. |
| `servers[].id` | yes | `^[a-z0-9][a-z0-9-]{1,63}$`. **Unique in the whole list and never changed or reused**: the app uses it to know which profile came from which entry. |
| `servers[].name` | yes | The service's own name as its provider calls it (max 80). A plain string, shown and used as the profile name in every language. |
| `servers[].description` | no | One short plain-text sentence in English (max 400). Shown as written in every language; keep it neutral and factual. |
| `servers[].blocks` | no | What the resolver blocks, as codes: `malware` (includes phishing), `ads`, `trackers`, `adult`, `social` (social media), `gambling`, `proxies` (proxy and VPN sites). Empty or missing = blocks nothing. The app translates these codes and uses them for the feature filter and for searching in the user's language. Another code (lower case letters, digits, hyphens, 2-24 characters, at most 8 codes) is accepted and shown as written until the app has a translation. |
| `servers[].logging` | no | `none`, `minimal`, `short-term`, `anonymized`, `configurable` or `unknown` (default `unknown`). Use what the provider states. |
| `servers[].v4`, `v6` | one of v4/v6/doh | Up to 4 addresses each, primary first. **Public addresses only**: private, loopback, link-local, unique-local, CGNAT and multicast ranges are refused. |
| `servers[].doh` | one of v4/v6/doh | DNS-over-HTTPS template, `https://` only, max 300 characters, none of `" ' \` ^ & \| < > %` or spaces. |
| `servers[].encryptedOnly` | no | `true` when the plain addresses refuse ordinary DNS and only answer DoH (Mullvad). Needs `doh`. The app then always uses encrypted DNS for it and fails closed. |
| `servers[].parameters` | no | Values the user fills in (NextDNS: the configuration id): a list of up to 4 **tokens** (upper-case words, `^[A-Z][A-Z0-9_]{1,31}$`) that **must occur in `doh`**; no token may contain another. The picker shows an empty text box labelled with the token itself (`CONFIG_ID`) on the entry's row; a value may only contain letters, digits, `-` and `_` (up to 64) and every token in `doh` is replaced by its value. The entry cannot be added until every field is filled in. A `doh` that still contains a placeholder such as `YOUR_` or `CONFIG_ID` without a `parameters` entry is refused. An item may also be written as `{ "token": "CONFIG_ID" }`. |

Plain text only: no control characters and no em dash in any text.

## What the app does with an entry

- A list entry becomes a profile: with addresses and `doh` it is **Auto** (DoH when Windows supports it, else plain); with only `doh` or with `encryptedOnly` it is **DoH** (and `encryptedOnly` also turns on *Require encryption*); without `doh` it is **Plain**. The first two IPv4 and the first two IPv6 addresses are used.
- The profile must also pass the same checks the background service applies to a profile before it touches the network adapter.
- "Already in your list" means a profile with the entry's id (`DnsProfile.ListId`), the same name, or the same first address and template.
- Nothing is applied automatically: the user ticks entries and presses **Apply**.
- The picker's details come from the fields: the description as written, "Blocks: ..." and "Logging: ..." in the user's language, the addresses, and the provider with the country name in the user's language. Searching matches the names, provider, description and the translated words.

## Changing the format later

- **Additive changes** (new fields, new providers, new `blocks` codes) keep `schemaVersion: 1`; an older app ignores what it does not know (an unknown `blocks` code shows as written).
- A provider file with **another `schemaVersion`** is skipped by an app that does not know it (the picker says how many files need a newer MasselGUARD) and the other files still work; an index with another `schemaVersion` is refused and the app keeps its downloaded copy. A file that is corrupt or missing makes the whole refresh fail (the list never mixes versions).
- A breaking change belongs in a new folder (for example `v2/index.json` and `v2/servers/`) while the old files stay for older apps.
