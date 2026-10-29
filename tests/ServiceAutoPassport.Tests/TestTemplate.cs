namespace ServiceAutoPassport.Tests;

internal static class TestTemplate
{
    public const string CardBlock = """
        <table>
          <tr><th>Название сервиса</th><th></th><td></td></tr>
          <tr><th>Команда</th><th></th><td></td></tr>
          <tr><th>Название сервиса в кубере или на сервере</th><th></th><td></td></tr>
          <tr><th>Сервис в кубере?</th><th></th><td></td></tr>
          <tr><th>Requests</th><th>CPU (доля от ядра), RAM (Mb)</th><td></td></tr>
          <tr><th>Limits</th><th>CPU (доля от ядра), RAM (Mb)</th><td></td></tr>
          <tr><th>Количество Pod</th><th></th><td>указываем количество</td></tr>
          <tr><th>Автомасштабирование</th><th></th><td>max N /нет</td></tr>
          <tr><th>Namespace в K8S</th><th></th><td>исходный namespace</td></tr>
          <tr><th>Репозиторий</th><th></th><td></td></tr>
          <tr><th>Папка в app-params</th><th></th><td></td></tr>
          <tr><th>Шаблон конфига</th><th></th><td></td></tr>
          <tr><th>Vault.Переменная с ключом сервиса</th><th></th><td></td></tr>
          <tr><th>Vault.Переменная с ключами для предоставления доступа</th><th></th><td></td></tr>
          <tr><th>Описание</th><th></th><td>Кратко назначение компонента</td></tr>
          <tr><th>Сетевая схема и согласование ДИБ</th><th></th><td></td></tr>
          <tr><th>Согласование схемы ДИБ</th><th></th><td></td></tr>
          <tr><th>Стандартный HealthCheck</th><th></th><td>да/нет</td></tr>
          <tr><th>Микрофронты (для BFF)</th><th></th><td></td></tr>
          <tr><th>Для доменного API</th><th></th><td></td></tr>
          <tr><th>app-params. Переменная содержащая адрес доменного API</th><th></th><td></td></tr>
        </table>
        """;

    public static string FullPage => $"""
        <img data-macro-name="toc" />
        <h2>Карточка элемента деплоя</h2>
        {CardBlock}
        <h2>Используемые микросервисы test</h2>
        <table>
          <tr><th>Название сервиса</th><th>Переменная адреса компонента в app-params</th><th>Комментарии</th></tr>
          <tr><td>Пример</td><td>SVC_OLD_HOST</td><td></td></tr>
        </table>
        <h2>Объекты доступа</h2>
        <table>
          <tr><th>Объект доступа</th><th>Переменная адреса в app-params</th><th>Ключи в Vault</th><th>Комментарии</th></tr>
          <tr><td>S3</td><td><p>S3_TEST_URL</p><p>S3_TEST_ACCESS_KEY</p></td><td>S3_TEST_SECRET_KEY</td><td></td></tr>
          <tr><td>Kafka test</td><td>KAFKA_UNUSED_HOST</td><td></td><td></td></tr>
        </table>
        <table data-macro-name="ui-expand" data-macro-parameters="title=Подсказка по объектам доступа">
          <tr><td>Подсказка по объектам доступа</td></tr>
        </table>
        <h2>Специфичные настраиваемые параметры</h2>
        <table>
          <tr><th>Описание</th><th>Название переменной/ключа</th><th>Secret(да/нет)</th><th>Значение по умолчанию</th></tr>
          <tr><td>БД</td><td>DB_PRO_PRODUCTS_DATABASE</td><td>Нет</td><td>ИМЯ_БД_СЕРВИСА</td></tr>
          <tr><td>Логгирование</td><td>SECTION_LOGGING</td><td>Нет</td><td>Старое значение</td></tr>
          <tr><td>Образец</td><td>OLD_SETTING</td><td>Нет</td><td>Подсказка</td></tr>
        </table>
        <h2>Дополнительная информация</h2>
        <p><br /></p>
        <table data-macro-name="table-excerpt" data-macro-parameters="hide=true|name=test_passport_microservice">
          <tr><td>Дополнительная информация</td></tr>
        </table>
        """;
}
