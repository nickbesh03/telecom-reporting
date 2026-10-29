import {defineConfig} from '@playwright/test';
export default defineConfig({testDir:'./e2e',workers:1,use:{baseURL:'http://127.0.0.1:5173'},webServer:[
{command:'dotnet run --project ../backend --no-launch-profile',url:'http://127.0.0.1:5080/api/health',env:{Database__Provider:'Sqlite',ConnectionStrings__Network:'Data Source=e2e.db',ASPNETCORE_URLS:'http://127.0.0.1:5080'},reuseExistingServer:!process.env.CI,timeout:120000},
{command:'npm run dev -- --port 5173',url:'http://127.0.0.1:5173',reuseExistingServer:!process.env.CI}
]});
