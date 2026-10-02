.PHONY: infra-up infra-down migrate migrate-dev-registry build test lint run-api run-worker run-simulator run-inmemory

SQL_USER     ?= sa
SQL_PASSWORD ?= Dev@Strong123
SQL_DB       ?= DB_COEXISTENCE_V2
SQLCMD        = docker exec -i convivencia-v2-sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U $(SQL_USER) -P "$(SQL_PASSWORD)" -C -b

infra-up:
	docker compose up -d

infra-down:
	docker compose down

# Applies infra/sql/*.sql. Scripts are idempotent (IF NOT EXISTS guards), so re-running is safe.
migrate:
	@$(SQLCMD) -Q "IF DB_ID('$(SQL_DB)') IS NULL CREATE DATABASE [$(SQL_DB)];"
	@for f in infra/sql/*.sql; do \
		echo "Applying $$f"; \
		$(SQLCMD) -d $(SQL_DB) -i /dev/stdin < $$f || exit 1; \
	done

# LOCAL ONLY: creates TB_DADOS_CONVIVENCIA in the same database. In every shared environment the
# table already exists there and is not owned by this solution. Run after `make migrate`.
migrate-dev-registry:
	@$(SQLCMD) -d $(SQL_DB) -i /dev/stdin < infra/dev/TB_DADOS_CONVIVENCIA.sql

build:
	dotnet build ConvivenciaPix.V2.sln

test:
	dotnet test ConvivenciaPix.V2.sln

lint:
	dotnet format ConvivenciaPix.V2.sln

run-simulator:
	dotnet run --project tools/BacenSpiSimulator/ConvivenciaPix.V2.BacenSpiSimulator.csproj

run-api:
	dotnet run --project src/SpiProxyApi/ConvivenciaPix.V2.SpiProxyApi.csproj

# The whole proxy (API + pull loop) in one process with in-memory storage: no Docker, no SQL
# Server, Redis or Kafka, and the SPI simulated in process. Nothing survives a restart.
run-inmemory:
	dotnet run --project src/SpiProxyApi/ConvivenciaPix.V2.SpiProxyApi.csproj --launch-profile inmemory

run-worker:
	dotnet run --project src/SpiInboundWorker/ConvivenciaPix.V2.SpiInboundWorker.csproj
