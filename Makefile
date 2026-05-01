.PHONY: infra-up infra-down migrate test lint run-api

infra-up:
	docker-compose up -d

infra-down:
	docker-compose down

migrate:
	dotnet ef database update --project src/Infrastructure/ConvivenciaPix.Infrastructure.csproj --startup-project src/SpiProxyApi/ConvivenciaPix.SpiProxyApi.csproj

test:
	dotnet test

lint:
	dotnet format

run-api:
	dotnet run --project src/SpiProxyApi/ConvivenciaPix.SpiProxyApi.csproj
