using System.Net;

namespace Klassenbibliothek.Services;

public static class TodoFormEmbedCode
{
    public static string Build(Guid formId, string? formName, string publicUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicUrl);

        var name = string.IsNullOrWhiteSpace(formName) ? "Formular" : formName.Trim();
        var encodedName = WebUtility.HtmlEncode(name);
        var encodedUrl = WebUtility.HtmlEncode(publicUrl);
        var suffix = formId.ToString("N");
        var headingId = $"sessage-form-heading-{suffix}";
        var frameId = $"sessage-form-frame-{suffix}";

        return $$"""
            <section aria-labelledby="{{headingId}}">
              <h2 id="{{headingId}}">{{encodedName}}</h2>

              <iframe
                id="{{frameId}}"
                src="{{encodedUrl}}"
                title="{{encodedName}}"
                style="display:block;width:100%;min-height:760px;border:0;border-radius:8px;"
                loading="lazy">
              </iframe>

              <p>
                <a href="{{encodedUrl}}">Formular als eigene Seite &ouml;ffnen</a>
              </p>
            </section>

            <script>
              (() => {
                const frame = document.getElementById("{{frameId}}");
                if (!frame) return;

                const expectedOrigin = new URL(frame.src, document.baseURI).origin;
                window.addEventListener("message", (event) => {
                  if (event.origin !== expectedOrigin || event.source !== frame.contentWindow) return;

                  const data = event.data;
                  if (!data || data.type !== "sessage:form-height" || data.version !== 1) return;

                  const height = Number(data.height);
                  if (!Number.isFinite(height) || height < 320 || height > 100000) return;

                  frame.style.height = `${Math.ceil(height)}px`;
                });
              })();
            </script>
            """;
    }
}
