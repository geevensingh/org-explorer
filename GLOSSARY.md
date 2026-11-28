# Org explorer

An individual's private reference for understanding people, formal reporting
relationships, and the context they have recorded about their organization.

## Language

### People and identity

**Person**:
An individual represented in the workspace, identified by an email address
assumed to be stable and unique within this context.
_Avoid_: Contact

**Directory name**:
The person's directory display name, distinct from the name the workspace owner
prefers to use for them.
_Avoid_: Preferred name

**Preferred name**:
The name the workspace owner chooses to use prominently when referring to a
person, while retaining their directory name.
_Avoid_: Alias

**Alias**:
The person's short company handle, distinct from their full email address and
preferred name.
_Avoid_: Nickname, alternate email

**Person category**:
A mutually exclusive, email-identity-based classification used to describe People
and group reporting headcounts, not verified legal employment or payroll status.
_Avoid_: Employment status, user role

**Employee**:
A Person in the default Person category when their email identity does not
denote any recognized category.
_Avoid_: Non-vendor, verified employee, full-time employee

**Vendor**:
A Person whose email identity denotes vendor affiliation under the workspace's
Person-category convention.
_Avoid_: Directory guest, every external worker

**Archived person**:
A person excluded from ordinary current-org views whose information and personal
context remain available.
_Avoid_: Deleted person, departed employee

### Reporting relationships

**Manager**:
The person to whom another person formally reports, regardless of job-title wording.
_Avoid_: Project lead, informal lead

**Direct report**:
A Person who formally reports to a Manager without an intervening manager.
_Avoid_: Any reporting descendant

**Reporting chain**:
A person's formal manager and all successive management ancestors.
_Avoid_: Informal team membership

**Reporting subtree**:
A manager together with everyone who reports directly or indirectly through them.
_Avoid_: Permanent team

**Reporting descendants**:
Everyone who reports directly or indirectly through a manager, excluding that
manager.
_Avoid_: Reporting subtree, direct reports

**Imported population**:
The people available in the workspace from selected reporting subtrees and their
ancestor context, rather than a presumed complete company directory.

### Personal context

**Person note**:
A private note about one person that is not inherited through reporting relationships.
_Avoid_: Personal note

**Team name**:
A private label assigned to a manager's reporting subtree, not an independently
identified team.
_Avoid_: Permanent-team identity, department

**Team note**:
A private note owned by a manager that supplies context for people in that
manager's current reporting subtree.
_Avoid_: Shared note, permanent-team note

**Ongoing note**:
A Person note or Team note designated as its owner's main continuing note of
that kind, without replacing the owner's other notes or changing its scope.
_Avoid_: Only note, merged note

**Historical team context**:
A preserved former reporting chain and the team notes known when that context
was closed; subsequent note edits do not change this historical context.
_Avoid_: Current inherited notes

**Workspace**:
An individual's private collection of people, notes, and observed organizational
history.
_Avoid_: Shared CRM

### Observations and changes

**Observation**:
An assertion about supplied person attributes at a stated time, not proof of the
exact time an underlying real-world change occurred.

**Profile change**:
A recorded difference between previously stored and newly accepted structured
person attributes.

**Import batch**:
Explicit person updates reviewed and accepted together; omission does not assert
that a person or attribute has disappeared.
_Avoid_: Complete snapshot, implicit replacement
