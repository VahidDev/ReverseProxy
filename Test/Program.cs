using Test.Middleware;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

//builder.Services.AddHttpClient("configured-disable-automatic-cookies")
//    .ConfigurePrimaryHttpMessageHandler(() =>
//    {
//        return new HttpClientHandler()
//        {
//            UseCookies = false,
//        };
//    });

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    //app.UseSwagger();
    //app.UseSwaggerUI();
}

app.UseCors(builder => builder
    .AllowAnyOrigin()
    .AllowAnyMethod()
    .AllowAnyHeader());

app.UseMiddleware<ReverseProxyMiddleware>();

//app.Run(async (context) =>
//{
//    await context.Response.WriteAsync("<a href='https://localhost:44385/customProxy?url=https%3a%2f%2fwww.google.com'>Sign in</a>");
//});

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
