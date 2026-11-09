# Security notes

File sharing is owner selected. Android grants one temporary local session to the authorized PC, and it ends on stop, expiry, phone lock or disconnect. The app does not save broad or persistent folder permissions. Selected folders are copied into a private bounded snapshot; their paths never become a general Windows-to-phone path API.

The Windows endpoint is loopback behind the specific ADB tunnel and bearer token. Folder ZIP names and entries are treated as untrusted: path traversal, links, collisions, dangerous names, excessive sizes/counts/depth and central-directory bombs are rejected. Extracted paths carry exact ownership records. Cache cleanup deletes only recorded app-owned items and preserves unknown files. User destination files are never included in cache cleanup.

Folder snapshots and metadata checks do not constitute a cryptographic source-integrity claim. Provider mutation checks cannot guarantee an external provider is immutable. A forced shutdown during Save can leave a marked hidden partial in the chosen destination; there is no broad destination cleanup. See `REVIEW.md` for details.
