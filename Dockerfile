# Build the app, parse the Labor Code PDF into the search index, then ship both
# in a slim ASP.NET runtime image. The index is rebuilt whenever data/raw changes.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/ src/
RUN dotnet publish src/LaborRag/LaborRag.csproj -c Release -o /app
COPY data/raw/ data/raw/
RUN dotnet /app/laborrag.dll --index /app/data/index ingest data/raw/

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV LABOR_RAG_INDEX=/app/data/index
# Listens on $PORT when the host sets it (Render does), otherwise on 8080.
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "laborrag.dll", "serve"]
