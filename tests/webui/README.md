# Web UI checks

Requires Node.js 24 or newer and Python 3.9 or newer. From the repository root:

```sh
npm ci --prefix tests/webui
npm test --prefix tests/webui
```

- `check.cjs`: 47 account, transaction, CSRF, maintenance lease, draft, credential, material and responsive markup checks.
- `locale.cjs`: 19 jsdom checks covering all three languages, all dictionary entries, language persistence and native events, dialogs, unsaved inputs, errors, and copy behavior.
- `navigation-css.cjs`: 252 navigation assertions across three languages and six viewport widths, plus a synthetic selector mutation to prove that hidden labels are detected.

The six runtime resources live in `webui/`. Fixtures use synthetic account and credential values. Catalog checks assert the shipped translation dictionaries and route contract without requiring original game master data, private accounts or image assets. Missing images use the localized fallback.

CSS checks resolve media rules through CSSOM and inspect computed styles in jsdom. These checks do not claim browser geometry or physical-device validation.
