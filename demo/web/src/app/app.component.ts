import { Component } from '@angular/core';
import { ChatRelatorioComponent } from './chat-relatorio';

/**
 * Casca de DEMONSTRAÇÃO imitando a página do painel (dashboard-receita): barra superior, cards brancos sobre
 * fundo cinza e o chat como último card. No painel, só entra a tag <app-chat-relatorio [municipio]="..." />.
 */
@Component({
  selector: 'app-root',
  standalone: true,
  imports: [ChatRelatorioComponent],
  template: `
    <header class="barra">
      <span class="icone-menu" aria-hidden="true"><i></i><i></i><i></i></span>
      <h1>Painel GPN - Demonstração</h1>
    </header>
    <main class="pagina">
      <div class="card vazio">Gráficos e tabelas do painel</div>
      <div class="card vazio">Gráficos e tabelas do painel</div>
      <!-- Sem [municipio]: o chat mostra o seletor com as cidades do banco. No painel: [municipio]="municipioSelecionado" -->
      <app-chat-relatorio class="largura-total" />
    </main>
  `,
  styles: [`
    .barra { display: flex; align-items: center; gap: 16px; height: 58px; padding: 0 16px; background: #fff; box-shadow: 0 1px 4px rgba(0,0,0,.15); }
    .barra h1 { flex: 1; margin: 0; text-align: center; font-size: 1.5rem; font-weight: 700; color: #1e8449; }
    .icone-menu { display: flex; flex-direction: column; gap: 4px; width: 22px; }
    .icone-menu i { display: block; height: 3px; background: #222; border-radius: 1px; }
    .pagina { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); gap: 12px; padding: 12px 8px; }
    .card { background: #fff; }
    .vazio { display: grid; place-items: center; height: 160px; color: #aaa; font-size: .9rem; }
    .largura-total { grid-column: 1 / -1; }
    @media (max-width: 800px) { .pagina { grid-template-columns: minmax(0, 1fr); } .barra h1 { font-size: 1.1rem; } }
  `],
})
export class AppComponent {}
