# Step templates (ADR-IR34 §7.0, decision 1): platform-sod-guard, platform-db-backup and platform-pin-writer, built from
# the canonical PowerShell 7 scripts octopus/step-templates/*.ps1 (docs/scripting.md). Each body is two comment lines
# followed by the script verbatim; the script's parameters default to the template parameters ($OctopusParameters).
#
# The app processes of this repository inline the same scripts between "# >>> octopus/step-templates/<name>.ps1" and
# "# <<< octopus/step-templates/<name>.ps1" markers, inside a script block that passes the step's inputs, instead of
# referencing the templates: how config as code refers to a space step template (ID and version inside a Git-stored
# process) is [UNVERIFIED], and an inline copy keeps each app's process self-contained. The offline drift tests
# (CAP-OCT-004, CAP-OCT-015) keep the copies equal to the scripts, and so to these templates. A later app may use either
# form.
#
# Parameter IDs are fixed UUIDs (the provider requires them; changing one breaks steps that already use the template).

locals {
  step_template_scripts = {
    for name in ["sod-guard", "db-backup", "pin-writer"] : name => file("${local.repo_root}/octopus/step-templates/${name}.ps1")
  }

  single_line = { "Octopus.ControlType" = "SingleLineText" }
  multi_line  = { "Octopus.ControlType" = "MultiLineText" }
  checkbox    = { "Octopus.ControlType" = "Checkbox" }
}

resource "octopusdeploy_step_template" "sod_guard" {
  name            = "platform-sod-guard"
  description     = "Separation of duties for one manual intervention (ADR-IR34 decision 23): Platform.SoDMode decides whether the deployment creator may approve, and Platform.InterventionTestMode whether the automation user may answer (only with a conformance:<run-id> or e2e:<run-id> reason). Source: octopus/step-templates/sod-guard.ps1."
  action_type     = "Octopus.Script"
  step_package_id = "Octopus.Script"
  packages        = []

  parameters = [
    {
      id               = "37595128-36fc-5ac0-82e3-08d52e766fd6"
      name             = "SodGuard.ApprovalStep"
      label            = "Approval step"
      help_text        = "Name (not slug) of the manual intervention whose answer is checked, for example Prod go/no-go."
      default_value    = "Prod go/no-go"
      display_settings = local.single_line
    },
    {
      id               = "e286baa3-5245-59a7-892c-3fb6596d5489"
      name             = "SodGuard.OtherSteps"
      label            = "Other interventions"
      help_text        = "Comma-separated names of other interventions of the deployment, checked only for answers by the automation user. Interventions that did not run are skipped."
      default_value    = ""
      display_settings = local.single_line
    },
    {
      id               = "aee438ed-b304-5753-be47-be8a803d15a6"
      name             = "SodGuard.CheckCreator"
      label            = "Apply the creator rule"
      help_text        = "True: the Platform.SoDMode rule applies to the approval step (the prod go/no-go). False: only the automation-user rule applies (UAT sign-off)."
      default_value    = "True"
      display_settings = local.checkbox
    },
  ]

  properties = {
    "Octopus.Action.Script.ScriptSource" = "Inline"
    "Octopus.Action.Script.Syntax"       = "PowerShell"
    "Octopus.Action.Script.ScriptBody" = join("\n", [
      "# Step template platform-sod-guard (octopus/terraform/step-templates.tf): octopus/step-templates/sod-guard.ps1",
      "# verbatim; its parameters default to the template parameters.",
      local.step_template_scripts["sod-guard"],
    ])
  }
}

resource "octopusdeploy_step_template" "db_backup" {
  name            = "platform-db-backup"
  description     = "Pre-release database backup (ADR-IR34 decision 1, CAP-OCT-015): creates a Job from CronJob db-backup-<app>-<env> in namespace platform-backup and waits for it. uat and prod only. Runs on the Kubernetes worker pool Platform.WorkerPool. Source: octopus/step-templates/db-backup.ps1."
  action_type     = "Octopus.Script"
  step_package_id = "Octopus.Script"
  packages        = []

  parameters = [
    {
      id               = "caebf329-a13d-5c74-b4bd-4b1868f4138d"
      name             = "DbBackup.App"
      label            = "App"
      help_text        = "App slug (apps/<app>.yaml)."
      default_value    = ""
      display_settings = local.single_line
    },
    {
      id               = "82bffd4b-eaae-5344-9360-e0afe2237216"
      name             = "DbBackup.Environment"
      label            = "Environment"
      help_text        = "uat or prod; tdd databases are disposable and never backed up."
      default_value    = "#{Octopus.Environment.Name}"
      display_settings = local.single_line
    },
    {
      id               = "e42ed997-4a4a-55df-931d-52b20ac2a978"
      name             = "DbBackup.TimeoutSeconds"
      label            = "Timeout (seconds)"
      help_text        = "Longest wait for the backup Job."
      default_value    = "1800"
      display_settings = local.single_line
    },
  ]

  properties = {
    "Octopus.Action.Script.ScriptSource" = "Inline"
    "Octopus.Action.Script.Syntax"       = "PowerShell"
    "Octopus.Action.Script.ScriptBody" = join("\n", [
      "# Step template platform-db-backup (octopus/terraform/step-templates.tf): octopus/step-templates/db-backup.ps1",
      "# verbatim; its parameters default to the template parameters.",
      local.step_template_scripts["db-backup"],
    ])
  }
}

# The token is not a parameter: the script reads the sensitive variable PinWriter.GitToken, which the app project
# (or its step) supplies when it switches to this writer. A sensitive parameter without a default is left out because
# provider 1.20.0 sends it as a plain empty default [UNVERIFIED round trip].
resource "octopusdeploy_step_template" "pin_writer" {
  name            = "platform-pin-writer"
  description     = "Fallback pin writer (ADR-IR34 decision 20): commits images[].newTag of gitops/apps/<app>/envs/<env>/<deployable>/kustomization.yaml as octopus-argocd-pin-bot and waits for Argo CD Application <app>-<deployable>-<env> to be Synced and Healthy. Needs the sensitive variable PinWriter.GitToken. Source: octopus/step-templates/pin-writer.ps1."
  action_type     = "Octopus.Script"
  step_package_id = "Octopus.Script"
  packages        = []

  parameters = [
    {
      id               = "179636d3-0914-58d2-98db-1344b34d151f"
      name             = "PinWriter.App"
      label            = "App"
      help_text        = "App slug (apps/<app>.yaml)."
      default_value    = ""
      display_settings = local.single_line
    },
    {
      id               = "22c6b08e-c26c-559a-ba06-3bda6503c9d5"
      name             = "PinWriter.Deployable"
      label            = "Deployable"
      help_text        = "Deployable name (gitops path segment), for example app. Kustomize deployables only."
      default_value    = "app"
      display_settings = local.single_line
    },
    {
      id               = "3ea73c68-d9a3-5077-a058-82f0c1c8e6cf"
      name             = "PinWriter.Environment"
      label            = "Environment"
      help_text        = "tdd, uat or prod."
      default_value    = "#{Octopus.Environment.Name}"
      display_settings = local.single_line
    },
    {
      id               = "e73c1a08-b0db-5b0a-a238-9f90ce055884"
      name             = "PinWriter.Images"
      label            = "Images"
      help_text        = "image=tag pairs separated by commas or new lines; images are names under apps/<app>/, for example web=#{Octopus.Action.Package[web].PackageVersion}."
      default_value    = ""
      display_settings = local.multi_line
    },
    {
      id               = "68dcc720-c807-585c-886d-5c9c90002361"
      name             = "PinWriter.RepoUrl"
      label            = "Environment repository"
      help_text        = "HTTPS URL of the environment repository; empty means the platform's environment repository."
      default_value    = ""
      display_settings = local.single_line
    },
    {
      id               = "f888eac1-4ed5-54cf-96a9-533cf8be375e"
      name             = "PinWriter.Branch"
      label            = "Branch"
      help_text        = "Branch Argo CD reads."
      default_value    = "main"
      display_settings = local.single_line
    },
    {
      id               = "414d5557-da2c-55bb-bf5c-897e57d4b921"
      name             = "PinWriter.TimeoutSeconds"
      label            = "Timeout (seconds)"
      help_text        = "Longest wait for Argo CD to report Synced and Healthy."
      default_value    = "900"
      display_settings = local.single_line
    },
  ]

  properties = {
    "Octopus.Action.Script.ScriptSource" = "Inline"
    "Octopus.Action.Script.Syntax"       = "PowerShell"
    "Octopus.Action.Script.ScriptBody" = join("\n", [
      "# Step template platform-pin-writer (octopus/terraform/step-templates.tf): octopus/step-templates/pin-writer.ps1",
      "# verbatim; its parameters default to the template parameters.",
      local.step_template_scripts["pin-writer"],
    ])
  }
}
