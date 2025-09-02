using SFTB_Demo.Interfaces;
using SFTB_Demo.Services;
using SFTB_Demo.Settings;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
// Configure SFTP settings
builder.Services.Configure<SftpConfig>(
    builder.Configuration.GetSection("SftpConfig"));

// Register SFTP service
builder.Services.AddScoped<ISftpService, SftpService>();
builder.Services.AddScoped<IEnhancedSftpService, EnhancedSftpService>();



var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();