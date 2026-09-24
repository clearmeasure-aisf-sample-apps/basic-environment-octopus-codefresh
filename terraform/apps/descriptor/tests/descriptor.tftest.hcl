# Offline test of the descriptor module (no provider): parsing, defaults, the vault-name formula and the rejections.
# Run: terraform -chdir=terraform/apps/descriptor init -backend=false && terraform -chdir=terraform/apps/descriptor test
# Expected vault names were computed independently: sha1("<subscription>/<app>/<env>")[0:4] (Python hashlib).

variables {
  subscription_id = "22222222-2222-2222-2222-222222222222"
}

run "defaults_for_a_minimal_descriptor" {
  command = plan

  variables {
    app  = "shopcart"
    tier = "nonprod"
    descriptor = {
      schema = 1
      name   = "shopcart"
    }
  }

  assert {
    condition     = output.environments == ["tdd", "uat"]
    error_message = "nonprod holds tdd and uat by default"
  }
  assert {
    condition     = output.vault_names == { tdd = "kv-shopcart-t-1bb8", uat = "kv-shopcart-u-630e" }
    error_message = "kv-<app>-<e>-<hash4> with hash4 = sha1(\"<subscription>/<app>/<env>\")[0:4]"
  }
  assert {
    condition     = !output.has_database && output.database_engine == null && length(output.secrets) == 0
    error_message = "no database and no secrets by default"
  }
  assert {
    condition     = !output.azure_account && !output.resource_group && !output.workload_identity && length(output.app_roles) == 0
    error_message = "no Azure access by default"
  }
  assert {
    condition     = output.status == "active"
    error_message = "status defaults to active"
  }
}

run "prod_holds_prod_only" {
  command = plan

  variables {
    app        = "shopcart"
    tier       = "prod"
    descriptor = { schema = 1, name = "shopcart" }
  }

  assert {
    condition     = output.environments == ["prod"] && output.vault_names == { prod = "kv-shopcart-p-a977" }
    error_message = "prod holds prod only"
  }
}

run "uppercase_subscription_hashes_like_lowercase" {
  command = plan

  variables {
    app             = "shopcart"
    tier            = "prod"
    subscription_id = "ABCDEF01-2345-6789-ABCD-EF0123456789"
    descriptor      = { schema = 1, name = "shopcart" }
  }

  assert {
    condition     = output.vault_names == { prod = "kv-shopcart-p-6684" }
    error_message = "the hash uses the lowercase subscription GUID"
  }
}

run "full_descriptor" {
  command = plan

  variables {
    app  = "shopcart"
    tier = "nonprod"
    descriptor = {
      schema       = 1
      name         = "shopcart"
      status       = "frozen"
      environments = ["tdd", "prod"]
      database     = { engine = "mssql-2022-express" }
      secrets      = [{ name = "api-key", generate = true }, { name = "vendor-token" }]
      octopus = {
        azureAccount = true
        projects     = [{ name = "shopcart" }, { name = "shopcart-jobs" }]
      }
      azure = {
        resourceGroup    = true
        workloadIdentity = true
        serviceAccount   = "web"
        roles            = ["Storage Blob Data Contributor"]
      }
    }
  }

  assert {
    condition     = output.environments == ["tdd"]
    error_message = "declared environments are intersected with the tier"
  }
  assert {
    condition     = output.has_database && output.database_engine == "mssql-2022-express"
    error_message = "database engine"
  }
  assert {
    condition     = output.secrets == [{ name = "api-key", generate = true }, { name = "vendor-token", generate = false }]
    error_message = "secrets with the generate default"
  }
  assert {
    condition     = tolist(output.octopus_projects) == tolist(["shopcart", "shopcart-jobs"]) && output.azure_account
    error_message = "Octopus projects and account"
  }
  assert {
    condition     = output.resource_group && output.workload_identity && output.service_account == "web" && tolist(output.app_roles) == tolist(["Storage Blob Data Contributor"])
    error_message = "Azure access"
  }
  assert {
    condition     = output.status == "frozen"
    error_message = "status passes through"
  }
}

run "committed_sandbox_descriptor" {
  command = plan

  variables {
    app  = "sandbox"
    tier = "nonprod"
  }

  assert {
    condition     = output.vault_names == { tdd = "kv-sandbox-t-cbe6", uat = "kv-sandbox-u-ced1" }
    error_message = "apps/sandbox.yaml parses and yields the sandbox vaults"
  }
  assert {
    condition     = output.has_database
    error_message = "the sandbox has a database"
  }
}

run "rejects_a_name_mismatch" {
  command = plan

  variables {
    app        = "shopcart"
    tier       = "nonprod"
    descriptor = { schema = 1, name = "othercart" }
  }

  expect_failures = [output.name]
}

run "rejects_a_reserved_slug" {
  command = plan

  variables {
    app        = "platform"
    tier       = "nonprod"
    descriptor = { schema = 1, name = "platform" }
  }

  expect_failures = [var.app]
}

run "rejects_a_role_outside_the_allowed_list" {
  command = plan

  variables {
    app  = "shopcart"
    tier = "nonprod"
    descriptor = {
      schema = 1
      name   = "shopcart"
      azure  = { resourceGroup = true, workloadIdentity = true, serviceAccount = "web", roles = ["Owner"] }
    }
  }

  expect_failures = [output.app_roles]
}

run "rejects_a_workload_identity_without_service_account" {
  command = plan

  variables {
    app  = "shopcart"
    tier = "nonprod"
    descriptor = {
      schema = 1
      name   = "shopcart"
      azure  = { workloadIdentity = true }
    }
  }

  expect_failures = [output.workload_identity]
}

run "rejects_a_platform_secret_name" {
  command = plan

  variables {
    app  = "shopcart"
    tier = "nonprod"
    descriptor = {
      schema  = 1
      name    = "shopcart"
      secrets = [{ name = "db-sa-password" }]
    }
  }

  expect_failures = [output.secrets]
}

run "rejects_more_than_twenty_projects" {
  command = plan

  variables {
    app  = "shopcart"
    tier = "nonprod"
    descriptor = {
      schema  = 1
      name    = "shopcart"
      octopus = { projects = [for i in range(21) : { name = "shopcart-p${i}" }] }
    }
  }

  expect_failures = [output.octopus_projects]
}
