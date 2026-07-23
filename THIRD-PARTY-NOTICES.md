# Third-party notices

BogMem includes software derived from
[MemPalace](https://github.com/MemPalace/mempalace), used under the following
license:

---

MIT License

Copyright (c) 2026 MemPalace Contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

---

BogMem embeds the `web2` American-English word corpus maintained and
distributed by [The FreeBSD Project](https://www.freebsd.org/). The corpus is
derived from *Webster's Second International Dictionary*; FreeBSD's source
notice states that its 1934 copyright has lapsed.

BogMem acknowledges Webster's Second International Dictionary as the original
source and The FreeBSD Project for maintaining and distributing the word list.
The corpus is pinned to FreeBSD source revision
[`df38c129b0ce967f6b8cca8d863f23915b315def`](https://github.com/freebsd/freebsd-src/blob/df38c129b0ce967f6b8cca8d863f23915b315def/share/dict/web2).
Its upstream SHA-256 is
`4240ed1b31a0e2f0ae9a475012d310bfffbd348a75401b760d60f3f623190f4e`.
Additional provenance and the embedded artifact checksum are recorded in
[`web2.NOTICE.md`](src/Bogmem.Slices/Spellcheck/data/web2.NOTICE.md), which is
also included in the NuGet tool package.
