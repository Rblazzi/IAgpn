// Liga src/app/chat-relatorio à pasta real da tela (frontend/chat-relatorio, a que vai para o painel).
// Assim a demonstração usa exatamente a mesma pasta, no mesmo lugar em que ela fica no painel.
// Roda sozinho antes de "npm start" e "npm run build" (junction no Windows, link simbólico no Linux/Mac).
const fs = require('fs');
const path = require('path');

const destino = path.resolve(__dirname, '../../frontend/chat-relatorio');
const atalho = path.resolve(__dirname, 'src/app/chat-relatorio');

try {
  if (fs.realpathSync(atalho) === fs.realpathSync(destino)) process.exit(0);
  console.error(`src/app/chat-relatorio existe mas não aponta para ${destino}. Apague-a e rode de novo.`);
  process.exit(1);
} catch {
  fs.symlinkSync(destino, atalho, 'junction');
  console.log(`src/app/chat-relatorio -> ${destino}`);
}
