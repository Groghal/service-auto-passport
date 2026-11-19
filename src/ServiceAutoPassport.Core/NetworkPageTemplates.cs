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
        """ + TransformerHtml + """
          </td>
        </tr></tbody></table>
        """;

    // Confluence editor markup from a copy of the Stiltsoft macro in edit mode.
    internal const string TransformerHtml = """
        <table class="wysiwyg-macro" data-macro-name="table-joiner"
          data-macro-parameters="dateFormat=dd M yy|decimals=|export-csv=false|export-pdf=false|export-print=false|export-word=false|isFirstTimeEnter=true|join-column=Элемент|plainText=false|separator=.|show-source=false|sql=SELECT * FROM T1|thousand-separator=|transpose=false|transposeResult=false|worklog=365\|5\|8\|y w d h m\|y w d h m"
          data-macro-schema-version="1" data-macro-body-type="RICH_TEXT" data-mce-resize="false">
          <tbody><tr><td class="wysiwyg-macro-body"><p><br /></p>
            <table class="confluenceTable" data-mce-resize="false"><tbody><tr>
              <th class="confluenceTh">Элемент</th>
            </tr></tbody></table>
          <p><br /></p></td></tr></tbody>
        </table>
        """;
}
