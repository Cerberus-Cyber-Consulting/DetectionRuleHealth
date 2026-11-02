# Vendored libraries

Pinned copies, served locally by `rulehealth serve`. The UI never loads anything from a CDN at
runtime, so customer data never leaves the machine.

| File | Package | License | Taken from |
|---|---|---|---|
| `alpine-3.14.9.min.js` | `alpinejs@3.14.9` (`dist/cdn.min.js`) | MIT (declared in the package's `package.json`; the npm package ships no LICENSE file) — https://github.com/alpinejs/alpine | npm tarball, integrity `sha512-gqSOhTEyryU9FhviN…` verified against the registry |
| `echarts-5.6.0.min.js` | `echarts@5.6.0` (`dist/echarts.min.js`) | Apache-2.0, see `echarts.LICENSE` and `echarts.NOTICE` | npm tarball, integrity `sha512-oTbVTsXfKuEhxftHq…` verified against the registry |
| `lucide-1.52.0.min.js` | `lucide@1.52.0` (UMD build, header `@license lucide v1.52.0 - ISC`) | ISC, see `LICENSE-lucide.txt` | Cerberus brand kit (2026-10-26); **not yet** checked against the registry integrity |

To update: download the tarball from `https://registry.npmjs.org/<name>/<version>`, check its SHA-512
against `dist.integrity` in the registry metadata, copy the file above, rename it with the new
version, and update the `<script>` tags in `ui/index.html`.
