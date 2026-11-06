import { ApplicationConfig, provideZoneChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { CHAT_RELATORIO_API_URL } from './chat-relatorio';
import { environment } from '../environments/environment';

export const appConfig: ApplicationConfig = {
  providers: [
    provideZoneChangeDetection({ eventCoalescing: true }),
    provideHttpClient(),
    // Endereço da API do módulo (no painel: o baseUrl do próprio painel)
    { provide: CHAT_RELATORIO_API_URL, useValue: environment.baseUrl },
  ],
};
