# Email as person identity

Treat email as a stable, unique person identity rather than requiring immutable
source-qualified directory IDs. This is an explicit domain assumption chosen for
the import contract; names, aliases, and other attributes may change without
changing identity. If email changes or is reused for another person, automatic
identity continuity is outside the supported assumption: do not silently invent
a name-based match or attach someone else's personal history.
