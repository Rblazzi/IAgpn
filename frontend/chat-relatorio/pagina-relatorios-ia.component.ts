import { Component, input } from '@angular/core';
import { ChatRelatorioComponent } from './chat-relatorio.component';

/**
 * Tela pronta "Relatórios com IA": o chat ocupando a página inteira.
 * Uso como rota do painel:
 *   { path: 'relatorios-ia', loadComponent: () => import('./chat-relatorio').then(m => m.PaginaRelatoriosIaComponent) }
 * Com `withComponentInputBinding()` no provideRouter, ?municipio=aguas-da-prata já abre no município.
 * Para colocar o chat numa tela que já existe, use só <app-chat-relatorio [municipio]="..." />.
 */
@Component({
  selector: 'app-pagina-relatorios-ia',
  standalone: true,
  imports: [ChatRelatorioComponent],
  template: `<app-chat-relatorio [municipio]="municipio() ?? null" />`,
  styles: [`
    :host { display: block; padding: 12px 8px; }
    app-chat-relatorio { --chat-altura: calc(100vh - 120px); }
  `],
})
export class PaginaRelatoriosIaComponent {
  /** Nome ("Águas da Prata") ou código ("aguas-da-prata"). Vazio: o chat mostra o seletor de municípios. */
  municipio = input<string | undefined>(undefined);
}
