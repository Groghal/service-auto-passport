namespace ServiceAutoPassport.Core;

public static class NetworkTableTemplates
{
    // Confluence editor markup from a copy of the Stiltsoft macro in edit mode.
    public const string BasicHtml = """
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
