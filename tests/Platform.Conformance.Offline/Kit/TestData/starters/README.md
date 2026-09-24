# Kit fixture starters

Fallback starters for CAP-KIT-001 (`OnboardingToolTests`). The test copies a role's starters from the repository
(`codefresh/templates/`, `octopus/templates/`, `gitops/templates/`) and uses these only when that folder is absent.
They follow the documented layout and tokens (contracts `starters`); path segments use the `__ENV__` and
`__DEPLOYABLE__` spellings because angle brackets are not valid in Windows file names.
