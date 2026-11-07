# textstats (declared third-party)

Clean base library plus one bundled stand-in third-party component under vendor/. The component has its own
license text (LICENSE.txt), notices (NOTICE.md) and a list of declared changes (CHANGES.md).

- Purpose: show a declared third-party component with source revision, license, notices and changes.
- Expected processing state: the declaration needs matching notice locations. The stand-in has no upstream
  project; the source URL and version in declaration.json are labelled stand-ins.
- Rights: the base library is authored for the demonstration (LICENSE.txt). The vendor component is MIT
  licensed by its authored license text.
