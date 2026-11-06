import { HttpClient } from '@angular/common/http';
import { Injectable, InjectionToken, inject } from '@angular/core';
import { Observable } from 'rxjs';

/**
 * Endereço da API onde o módulo do backend está (com "/" no fim). Padrão: "/" (mesmo endereço do site).
 * No painel, informar no app.config.ts:
 *   { provide: CHAT_RELATORIO_API_URL, useValue: environment.baseUrl }
 */
export const CHAT_RELATORIO_API_URL = new InjectionToken<string>('CHAT_RELATORIO_API_URL', {
  providedIn: 'root',
  factory: () => '/',
});

export interface MensagemChat {
  papel: 'user' | 'assistant';
  texto: string;
}

export interface Disponibilidade {
  municipio: string;
  ano: number;
  mesMaxReceita: number | null;
  mesMaxDespesa: number | null;
  /** Código do município no banco (ex.: "aguas-da-prata"). */
  codigo: string;
}

export interface ArquivoGerado {
  formato: 'docx' | 'pdf';
  url: string;
  tamanho: number;
}

export interface RelatorioGerado {
  id: string;
  nomeArquivo: string;
  arquivos: ArquivoGerado[];
}

export interface ConsultaExecutada {
  id: string;
  objetivo: string;
  sql: string;
  linhas: number;
  erro: string | null;
}

export interface ParametrosRelatorio {
  municipio: string;
  ano: number;
  mesInicial: number;
  mesFinal: number;
  anoComparacao: number | null;
  incluirReceitas: boolean;
  incluirDespesas: boolean;
}

export interface ChatResponse {
  resposta: string;
  relatorio: RelatorioGerado | null;
  parametros: ParametrosRelatorio | null;
  consultas: ConsultaExecutada[] | null;
  motor: string;
  tipo: 'relatorio' | 'resposta' | 'esclarecer' | 'erro';
  /** Sugestões de próximas perguntas, baseadas no pedido atual. */
  sugestoes: string[] | null;
}

export interface StatusPedido {
  id: string;
  status: 'fila' | 'processando' | 'concluido' | 'erro';
  etapa: string;
  segundos: number;
  resultado: ChatResponse | null;
  erro: string | null;
}

/** Mesmo padrão do painel: baseUrl + rota da API (ex.: api/Chat/). */
@Injectable({ providedIn: 'root' })
export class ChatRelatorioService {
  private http = inject(HttpClient);
  private baseUrl = (u => u.endsWith('/') ? u : u + '/')(inject(CHAT_RELATORIO_API_URL));

  static readonly CHAT = 'api/chat';
  static readonly RELATORIOS = 'api/relatorios/';

  static readonly CATALOGO = 'api/dados/catalogo';

  /** Inicia o pedido em segundo plano (relatórios podem levar minutos; evita timeout do IIS). */
  /** relatorioAnterior: id do último relatório da conversa (permite alterações, ex.: trocar a assinatura). */
  iniciar(mensagem: string, historico: MensagemChat[], municipio: string | null, relatorioAnterior: string | null = null): Observable<{ id: string }> {
    return this.http.post<{ id: string }>(`${this.baseUrl}${ChatRelatorioService.CHAT}/iniciar`, { mensagem, historico, municipio, relatorioAnterior });
  }

  status(id: string): Observable<StatusPedido> {
    return this.http.get<StatusPedido>(`${this.baseUrl}${ChatRelatorioService.CHAT}/${id}`);
  }

  cancelar(id: string): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}${ChatRelatorioService.CHAT}/${id}`);
  }

  /** Municípios/anos disponíveis no banco (no painel, usar o município já selecionado em "selecionar-municipio"). */
  catalogo(): Observable<Disponibilidade[]> {
    return this.http.get<Disponibilidade[]>(`${this.baseUrl}${ChatRelatorioService.CATALOGO}`);
  }

  /** Baixa como blob (passa pelos interceptors de autenticação do painel). */
  baixar(id: string, formato: 'docx' | 'pdf'): Observable<Blob> {
    return this.http.get(`${this.baseUrl}${ChatRelatorioService.RELATORIOS}${id}`, {
      params: { formato },
      responseType: 'blob',
    });
  }
}
