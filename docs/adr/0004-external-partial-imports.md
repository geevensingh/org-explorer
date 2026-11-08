# External partial-update import contract

The app accepts a documented versioned person-update format; other tools own GAL
authentication and extraction. Choose explicit partial updates rather than
complete-snapshot replacement: omissions preserve values, clears and archives are
explicit, and all structured profile fields are importable except notes.
Whole-batch validation and review protect relationship/history consistency while
requiring producers to supply valid references and deliberate changes instead of
the receiver guessing what an incomplete export means.
