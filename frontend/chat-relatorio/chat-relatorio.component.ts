import { Component, ElementRef, OnDestroy, ViewChild, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ArquivoGerado, ChatRelatorioService, ChatResponse, ConsultaExecutada, Disponibilidade, MensagemChat, RelatorioGerado } from './chat-relatorio.service';

interface ItemConversa extends MensagemChat {
  relatorio?: RelatorioGerado | null;
  consultas?: ConsultaExecutada[] | null;
  sugestoes?: string[] | null;
  erro?: boolean;
  hora: Date;
}

@Component({
  selector: 'app-chat-relatorio',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './chat-relatorio.component.html',
  styleUrl: './chat-relatorio.component.css',
})
export class ChatRelatorioComponent implements OnDestroy {
  private api = inject(ChatRelatorioService);

  // ---- tempo de espera: decorrido + estimativa (média dos últimos pedidos neste navegador)
  private static readonly CHAVE_TEMPOS = 'chatRelatorio.tempos';
  private timer?: ReturnType<typeof setInterval>;
  private inicio = 0;
  decorrido = signal(0); // segundos
  private tempos = signal<number[]>(this.lerTempos());

  /** Estimativa em segundos: média dos últimos 10 pedidos; sem histórico, 120 s. */
  estimativa = computed(() => {
    const t = this.tempos();
    return t.length ? Math.round(t.reduce((a, b) => a + b, 0) / t.length) : 120;
  });

  textoEstimativa = computed(() => {
    const t = this.tempos();
    if (!t.length) return 'estimativa: 1 a 3 min';
    const min = Math.max(5, Math.min(...t)), max = Math.max(...t);
    const fmt = (s: number) => (s < 60 ? `${s} s` : `${(s / 60).toFixed(s % 60 ? 1 : 0).replace('.', ',')} min`);
    return t.length < 3 ? `estimativa: ~${fmt(this.estimativa())}` : `estimativa: ${fmt(min)} a ${fmt(max)}`;
  });

  private pedidoAtual: string | null = null;
  private poll?: ReturnType<typeof setTimeout>;
  /** Etapa real informada pelo servidor (ex.: "Consultando o banco de dados (2 consulta(s))…"). */
  etapaServidor = signal('');

  etapa = computed(() => {
    const d = this.decorrido(), e = this.estimativa();
    if (d > e * 1.5 && d > 60) return `${this.etapaServidor() || 'Processando…'} (a IA está mais lenta que o normal)`;
    if (this.etapaServidor()) return this.etapaServidor();
    if (d < Math.min(15, e * 0.25)) return 'Entendendo o pedido…';
    if (d < e * 0.6) return 'Consultando o banco de dados…';
    return 'Montando a resposta e o relatório (Word e PDF)…';
  });

  /** Barra de progresso aproximada (nunca chega a 100% antes da resposta). */
  progresso = computed(() => Math.min(95, Math.round((this.decorrido() / this.estimativa()) * 100)));

  relogio(s: number) {
    return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
  }

  private iniciarTimer() {
    this.inicio = Date.now();
    this.decorrido.set(0);
    clearInterval(this.timer);
    this.timer = setInterval(() => this.decorrido.set(Math.floor((Date.now() - this.inicio) / 1000)), 1000);
  }

  private pararTimer(registrar: boolean) {
    clearInterval(this.timer);
    if (!registrar) return;
    const s = Math.round((Date.now() - this.inicio) / 1000);
    const lista = [...this.tempos(), s].slice(-10);
    this.tempos.set(lista);
    try { localStorage.setItem(ChatRelatorioComponent.CHAVE_TEMPOS, JSON.stringify(lista)); } catch {}
  }

  private lerTempos(): number[] {
    try {
      const v = JSON.parse(localStorage.getItem(ChatRelatorioComponent.CHAVE_TEMPOS) ?? '[]');
      return Array.isArray(v) ? v.filter((n) => typeof n === 'number' && n > 0) : [];
    } catch {
      return [];
    }
  }

  ngOnDestroy() {
    clearInterval(this.timer);
    clearTimeout(this.poll);
  }

  @ViewChild('lista') lista?: ElementRef<HTMLDivElement>;

  private static saudacao(): ItemConversa {
    return {
      papel: 'assistant',
      texto:
        'Olá! Sou o assistente de relatórios gerenciais. Diga o que você precisa e eu monto o documento em ' +
        '**Word e PDF** com os dados do TCESP, ou respondo direto aqui no chat.',
      hora: new Date(),
    };
  }

  mensagens = signal<ItemConversa[]>([ChatRelatorioComponent.saudacao()]);
  texto = '';
  carregando = signal(false);
  baixando = signal<string | null>(null);
  motor = signal<string>('');
  menuAberto = signal(false);

  /**
   * Município da página do painel (ex.: <app-chat-relatorio [municipio]="municipioSelecionado" />).
   * Aceita o nome ("Águas da Prata") ou o código ("aguas-da-prata"). Sem ele, aparece um seletor (demonstração).
   */
  municipio = input<string | null>(null);
  municipios = signal<string[]>([]);
  municipioSelecionado = signal<string | null>(null);
  private catalogoBanco = signal<Disponibilidade[]>([]);
  /** Nome de exibição do município em uso (converte o código do banco, se for o caso). */
  municipioAtual = computed(() => {
    const m = this.municipio() ?? this.municipioSelecionado();
    if (!m) return null;
    const alvo = m.toLowerCase();
    return this.catalogoBanco().find((c) => c.codigo === alvo || c.municipio.toLowerCase() === alvo)?.municipio ?? m;
  });

  constructor() {
    this.api.catalogo().subscribe({
      next: (cat) => {
        this.catalogoBanco.set(cat);
        const lista = [...new Set(cat.map((c) => c.municipio))];
        this.municipios.set(lista);
        let salvo: string | null = null;
        try { salvo = localStorage.getItem('chatRelatorio.municipio'); } catch {}
        this.municipioSelecionado.set(salvo && lista.includes(salvo) ? salvo : lista[0] ?? null);
      },
    });
  }

  trocarMunicipio(m: string) {
    this.municipioSelecionado.set(m);
    try { localStorage.setItem('chatRelatorio.municipio', m); } catch {}
  }

  /** Exemplos montados com os anos/meses que existem no banco para o município. */
  sugestoesIniciais = computed(() => {
    const alvo = this.municipioAtual()?.toLowerCase();
    const cat = this.catalogoBanco().filter((c) => !alvo || c.municipio.toLowerCase() === alvo);
    if (!cat.length) return [];
    const ultimo = cat.reduce((a, b) => (b.ano > a.ano ? b : a));
    const ano = ultimo.ano;
    const mes = Math.min(ultimo.mesMaxReceita ?? 12, ultimo.mesMaxDespesa ?? 12);
    const meses = ['janeiro', 'fevereiro', 'março', 'abril', 'maio', 'junho', 'julho', 'agosto', 'setembro', 'outubro', 'novembro', 'dezembro'];
    const temAnterior = cat.some((c) => c.ano === ano - 1);
    return [
      `Relatório gerencial de janeiro a ${meses[mes - 1]} de ${ano}${temAnterior ? ` comparando com ${ano - 1}` : ''}`,
      `Quais os 10 maiores fornecedores da saúde em ${temAnterior ? ano - 1 : ano}?`,
      `Quanto foi arrecadado de IPTU e ISS em ${temAnterior ? ano - 1 : ano}?`,
      `Projeção dos gastos com saúde até o final de ${ano + 1}`,
    ];
  });

  novaConversa() {
    this.menuAberto.set(false);
    if (this.carregando()) return;
    this.mensagens.set([ChatRelatorioComponent.saudacao()]);
    this.texto = '';
  }

  enviar(texto?: string) {
    const msg = (texto ?? this.texto).trim();
    if (!msg || this.carregando()) return;

    // Histórico enviado à IA: só as trocas de texto anteriores (sem a saudação inicial)
    const historico: MensagemChat[] = this.mensagens()
      .slice(1)
      .filter((m) => !m.erro)
      .map(({ papel, texto }) => ({ papel, texto }));

    this.mensagens.update((l) => [...l, { papel: 'user', texto: msg, hora: new Date() }]);
    this.texto = '';
    this.carregando.set(true);
    this.iniciarTimer();
    this.rolar();

    this.etapaServidor.set('');
    const ultimoRelatorio = [...this.mensagens()].reverse().find((m) => m.relatorio)?.relatorio?.id ?? null;
    this.api.iniciar(msg, historico, this.municipioAtual(), ultimoRelatorio).subscribe({
      next: ({ id }) => this.acompanhar(id),
      error: (e) => this.falhar(e),
    });
  }

  /** Consulta o andamento do pedido a cada 1 s até concluir. Tolera falhas de rede pontuais. */
  private acompanhar(id: string, falhasSeguidas = 0) {
    this.pedidoAtual = id;
    this.poll = setTimeout(() => {
      this.api.status(id).subscribe({
        next: (s) => {
          if (this.pedidoAtual !== id) return;
          this.etapaServidor.set(s.etapa);
          if (s.status === 'concluido' && s.resultado) return this.concluir(s.resultado);
          if (s.status === 'erro') return this.falhar(s.erro ?? 'erro ao processar', true);
          this.acompanhar(id);
        },
        error: (e) => (falhasSeguidas < 5 ? this.acompanhar(id, falhasSeguidas + 1) : this.falhar(e)),
      });
    }, 1000);
  }

  private concluir(r: ChatResponse) {
    this.pararTimer(true);
    this.pedidoAtual = null;
    this.motor.set(r.motor);
    this.mensagens.update((l) => [
      ...l,
      { papel: 'assistant', texto: r.resposta, relatorio: r.relatorio, consultas: r.consultas, sugestoes: r.sugestoes, hora: new Date() },
    ]);
    this.carregando.set(false);
    this.rolar();
  }

  private falhar(e: any, textoPronto = false) {
    this.pararTimer(false);
    this.pedidoAtual = null;
    const detalhe = textoPronto ? String(e) : typeof e?.error === 'string' ? e.error : e?.message ?? 'erro desconhecido';
    this.mensagens.update((l) => [
      ...l,
      { papel: 'assistant', texto: textoPronto ? detalhe : `Não consegui processar o pedido (${detalhe}). Tente novamente.`, erro: true, hora: new Date() },
    ]);
    this.carregando.set(false);
    this.rolar();
  }

  cancelar() {
    const id = this.pedidoAtual;
    if (!id) return;
    clearTimeout(this.poll);
    this.pedidoAtual = null;
    this.api.cancelar(id).subscribe({ error: () => {} });
    this.pararTimer(false);
    this.mensagens.update((l) => [...l, { papel: 'assistant', texto: 'Pedido cancelado.', erro: true, hora: new Date() }]);
    this.carregando.set(false);
  }

  baixar(r: RelatorioGerado, arq: ArquivoGerado) {
    this.baixando.set(r.id + arq.formato);
    this.api.baixar(r.id, arq.formato).subscribe({
      next: (blob) => {
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = `${r.nomeArquivo}.${arq.formato}`;
        a.click();
        URL.revokeObjectURL(url);
        this.baixando.set(null);
      },
      error: () => {
        this.baixando.set(null);
        alert('Não foi possível baixar o arquivo. Gere o relatório novamente.');
      },
    });
  }

  teclado(ev: KeyboardEvent) {
    if (ev.key === 'Enter' && !ev.shiftKey) {
      ev.preventDefault();
      this.enviar();
    }
  }

  /** Escapa HTML e aplica **negrito** e quebras de linha. */
  formatar(texto: string): string {
    const esc = texto.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    return esc.replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>').replace(/\n/g, '<br>');
  }

  tamanho(bytes: number) {
    return bytes > 1024 * 1024 ? `${(bytes / 1024 / 1024).toFixed(1)} MB` : `${Math.round(bytes / 1024)} KB`;
  }

  private rolar() {
    setTimeout(() => this.lista?.nativeElement.scrollTo({ top: this.lista.nativeElement.scrollHeight, behavior: 'smooth' }));
  }
}
