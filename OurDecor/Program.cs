using Microsoft.EntityFrameworkCore;
using OurDecor;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services.AddRazorPages();

builder.Services.AddRazorPages();
builder.Services.AddControllers(); // обязательно
builder.Services.AddDbContext<AppDbContext>(op => op.UseNpgsql(config.GetConnectionString("Conect")));
builder.Services.AddAuthorization();
builder.Services.AddAuthentication("Cookies")
    .AddCookie("Cookies", options =>
    {
        options.LoginPath = "/login"; // путь куда редиректить неавторизованных
        options.AccessDeniedPath = "/access-denied";
    });

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}
foreach (var c in app.Services.GetRequiredService<Microsoft.AspNetCore.Mvc.Infrastructure.IActionDescriptorCollectionProvider>().ActionDescriptors.Items)
{
    Console.WriteLine("Route: " + c.AttributeRouteInfo?.Template);
}

app.UseAuthentication();
app.UseAuthorization();

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();
app.MapControllers(); // обязательно
app.UseAuthorization();

app.MapRazorPages();

app.Run();
