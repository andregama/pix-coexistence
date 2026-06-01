.PHONY: infra-up infra-down migrate test lint run-api

SQL_HOST     ?= localhost,1433
SQL_USER     ?= sa
SQL_PASSWORD ?= Dev@Strong123
SQL_DB       ?= DB_COEXISTENCE

infra-up:
	docker-compose up -d

infra-down:
	docker-compose down

# Applies infra/sql/*.sql against the SQL Server container. Scripts are idempotent
# (IF NOT EXISTS guards) so re-running is safe. Uses the sqlcmd shipped inside the
# convivencia-sqlserver container to avoid a host-side sqlcmd install.
migrate:
	@docker exec -i convivencia-sqlserver /opt/mssql-tools18/bin/sqlcmd \
		-S localhost -U $(SQL_USER) -P "$(SQL_PASSWORD)" -C -b \
		-Q "IF DB_ID('$(SQL_DB)') IS NULL CREATE DATABASE [$(SQL_DB)];"
	@for f in infra/sql/*.sql; do \
		echo "Applying $$f"; \
		docker exec -i convivencia-sqlserver /opt/mssql-tools18/bin/sqlcmd \
			-S localhost -U $(SQL_USER) -P "$(SQL_PASSWORD)" -C -b -d $(SQL_DB) -i /dev/stdin < $$f || exit 1; \
	done

test:
	dotnet test

lint:
	dotnet format

run-api:
	dotnet run --project src/SpiProxyApi/ConvivenciaPix.SpiProxyApi.csproj
