import { defineConfig } from 'vite';
export default defineConfig({server:{port:5173,strictPort:true,proxy:{'/api-proxy':{target:'http://127.0.0.1:5080',changeOrigin:false}}},build:{outDir:'../../server/School.Bff/wwwroot',emptyOutDir:true,sourcemap:false}});
