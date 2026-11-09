namespace ServiceAutoPassport.Core;

public static class NetworkPageTemplates
{
    public const string BasicHtml = """
        <h2>Сетевая карта</h2>
        <table style="width:100%"><tbody><tr>
          <td style="width:40%;vertical-align:top">
            <div data-network-diagram-placeholder="true">
              <p><strong>Диаграмма draw.io</strong></p>
              <p>Добавьте сюда новый макрос draw.io. Скопируйте XML из приложения,
              откройте Extras → Edit Diagram в редакторе диаграммы и вставьте XML.</p>
            </div>
          </td>
          <td style="width:60%;vertical-align:top">
            <table class="confluenceTable"><tbody><tr>
              <th class="confluenceTh">Подключение</th><th class="confluenceTh">Параметры</th>
            </tr></tbody></table>
          </td>
        </tr></tbody></table>
        """;
}
