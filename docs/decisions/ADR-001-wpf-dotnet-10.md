# ADR-001: WPF sobre .NET 10 para o aplicativo Windows

- **Status:** Aceita
- **Data:** 2026-09-27

## Contexto

O Agent precisa executar em Windows 10/11 x64, integrar UI Automation e Win32, desenhar overlays transparentes, permanecer em background e oferecer um configurador visual. Foram consideradas WPF, WinUI 3 e WinForms.

## Decisão

Usar C# com .NET 10 LTS. Bibliotecas independentes de Windows terão `net10.0`; projetos Windows terão `net10.0-windows` e plataforma x64. Usar WPF no Desktop, overlay e TestTarget. Manter o núcleo sem dependência de WPF.

## Consequências

WPF reduz o risco de integração com APIs desktop maduras e permite evoluir a interface em XAML. O projeto aceita uma aparência menos nativa do WinUI em troca de menor complexidade inicial. Recursos modernos do Windows poderão ser incorporados pontualmente sem migrar o núcleo.

