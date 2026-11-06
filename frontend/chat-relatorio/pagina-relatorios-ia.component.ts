import { Component, input } from '@angular/core';
import { ChatRelatorioComponent } from './chat-relatorio.component';

/**
 * TELA DO CHAT (é esta que vai no módulo do painel).
 *
 * Opção 1 — como rota do módulo:
 *   import { PaginaRelatoriosIaComponent } from './chat-relatorio/pagina-relatorios-ia.component';
 *   { path: 'relatorios-ia', component: PaginaRelatoriosIaComponent }
 *
 * Opção 2 — dentro de uma tela que já existe (importar PaginaRelatoriosIaComponent no componente):
 *   <app-pagina-relatorios-ia />                         → mostra o seletor de municípios
 *   <app-pagina-relatorios-ia municipio="aguas-da-prata" /> → já abre no município (nome ou código)
 */
@Component({
  selector: 'app-pagina-relatorios-ia',
  standalone: true,
  imports: [ChatRelatorioComponent],
  template: `<app-chat-relatorio [municipio]="municipio() ?? null" />`,
  styles: [`
    :host { display: block; padding: 12px 8px; }
    app-chat-relatorio { --chat-altura: calc(100vh - 24px); }
  `],
})
export class PaginaRelatoriosIaComponent {
  /** Nome ("Águas da Prata") ou código ("aguas-da-prata"). Vazio: o chat mostra o seletor de municípios. */
  municipio = input<string | undefined>(undefined);
}
